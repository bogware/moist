using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Jobs;

/// <summary>
/// Thread-safe job queue with dependency resolution.
/// Manages the full lifecycle of encoding jobs.
/// </summary>
public sealed class JobQueue : IDisposable
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCts = new();
    private readonly List<IJobProcessor> _processors = [];
    private readonly List<Func<IJobProcessor>> _processorFactories = [];
    private readonly object _processorsLock = new();
    private readonly SemaphoreSlim _workerSemaphore;
    private readonly ILogger<JobQueue> _logger;
    private CancellationTokenSource? _cts;
    private readonly object _startLock = new();
    private Task? _runningTask;
    private readonly ConcurrentQueue<Job> _pendingQueue = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);

    public event EventHandler<Job>? JobAdded;
    public event EventHandler<Job>? JobStatusChanged;
    public event EventHandler<(Job Job, JobProgress Progress)>? JobProgressUpdated;

    private int _maxConcurrentJobs;
    public int MaxConcurrentJobs
    {
        get => _maxConcurrentJobs;
        set
        {
            if (value < 1) value = 1;
            if (value == _maxConcurrentJobs) return;
            var old = _maxConcurrentJobs;
            _maxConcurrentJobs = value;

            // Adjust the semaphore to reflect the new limit.
            // If increasing, release extra slots. If decreasing, the semaphore
            // will naturally throttle as permits are consumed.
            if (value > old)
            {
                var diff = value - old;
                _workerSemaphore.Release(diff);
            }
            _logger.LogInformation("MaxConcurrentJobs changed from {Old} to {New}", old, value);
        }
    }

    public IReadOnlyDictionary<string, Job> Jobs => _jobs;
    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;

    public JobQueue(int maxConcurrentJobs = 1, ILoggerFactory? loggerFactory = null)
    {
        _maxConcurrentJobs = maxConcurrentJobs;
        _workerSemaphore = new SemaphoreSlim(maxConcurrentJobs, int.MaxValue);
        _logger = loggerFactory?.CreateLogger<JobQueue>() ?? NullLogger<JobQueue>.Instance;
        LoggerFactory = loggerFactory;
    }

    /// <summary>Shared logger factory for processors to use.</summary>
    internal ILoggerFactory? LoggerFactory { get; }

    public void RegisterProcessor(IJobProcessor processor)
    {
        if (processor is CommandLineProcessor clp)
            clp.SetLogger(LoggerFactory);
        lock (_processorsLock)
            _processors.Add(processor);
    }

    /// <summary>
    /// Register a processor factory. A fresh instance is created per job to avoid shared mutable state
    /// (stopwatches, frame counters) between concurrent jobs.
    /// </summary>
    public void RegisterProcessorFactory(Func<IJobProcessor> factory)
    {
        lock (_processorsLock)
            _processorFactories.Add(factory);
    }

    public void Enqueue(Job job)
    {
        job.Status = JobStatus.Queued;
        _jobs[job.Id] = job;
        _logger.LogInformation("Job enqueued: {JobId} ({JobType})", job.Id, job.JobType);
        RaiseEvent(JobAdded, job);

        // If the queue is already running, signal the dispatcher to pick up new jobs
        if (IsRunning)
        {
            _pendingQueue.Enqueue(job);
            _pendingSignal.Release();
        }
    }

    /// <summary>Remove a job from the queue. If running, cancels it.</summary>
    public void Remove(string jobId)
    {
        CancelJob(jobId);
        _jobs.TryRemove(jobId, out _);
    }

    /// <summary>Cancel a specific running or queued job without removing it from the queue.</summary>
    public void CancelJob(string jobId)
    {
        if (_jobCts.TryRemove(jobId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (_jobs.TryGetValue(jobId, out var job) &&
            job.Status is JobStatus.Running or JobStatus.Queued)
        {
            job.Status = JobStatus.Cancelled;
            RaiseEvent(JobStatusChanged, job);
        }
    }

    public async Task StartAsync()
    {
        Task allTasks;
        lock (_startLock)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();

            var ct = _cts.Token;

            // Launch tasks for all currently queued jobs
            var tasks = new List<Task>();
            var pendingJobs = _jobs.Values
                .Where(j => j.Status == JobStatus.Queued)
                .ToList();

            foreach (var job in pendingJobs)
                tasks.Add(ProcessJobAsync(job, ct));

            // Also start a dispatcher that picks up newly enqueued jobs
            tasks.Add(DispatchNewJobsAsync(ct));

            allTasks = Task.WhenAll(tasks);
            _runningTask = allTasks;
        }

        try
        {
            await allTasks;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via Stop()
        }
    }

    /// <summary>
    /// Background loop that picks up jobs enqueued while the queue is running.
    /// </summary>
    private async Task DispatchNewJobsAsync(CancellationToken ct)
    {
        var dispatched = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pendingSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_pendingQueue.TryDequeue(out var job))
            {
                if (job.Status == JobStatus.Queued)
                    dispatched.Add(ProcessJobAsync(job, ct));
            }
        }

        // Wait for any dynamically dispatched jobs to finish
        if (dispatched.Count > 0)
        {
            try { await Task.WhenAll(dispatched); }
            catch (OperationCanceledException) { }
        }
    }

    public void Stop()
    {
        lock (_startLock)
        {
            _cts?.Cancel();
            // Don't dispose _cts here — running tasks may still reference it.
            // It will be disposed on next StartAsync() or in Dispose().
        }
    }

    public void Dispose()
    {
        Stop();
        _runningTask?.Wait(TimeSpan.FromSeconds(5));
        _cts?.Dispose();
        _cts = null;
        _workerSemaphore.Dispose();
        _pendingSignal.Dispose();

        // Dispose all per-job CTS
        foreach (var cts in _jobCts.Values)
            cts.Dispose();
        _jobCts.Clear();
    }

    private async Task ProcessJobAsync(Job job, CancellationToken queueCt)
    {
        // Create per-job CTS linked to the queue-level CTS
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(queueCt);
        _jobCts[job.Id] = jobCts;
        var ct = jobCts.Token;

        // Wait for dependencies
        try
        {
            while (job.DependsOn.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                // Check if any dependency doesn't exist in the queue — treat as failed
                var missingDep = job.DependsOn.FirstOrDefault(depId => !_jobs.ContainsKey(depId));
                if (missingDep is not null)
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = $"Dependency not found: {missingDep}";
                    _logger.LogError("Job {JobId} has missing dependency: {DepId}", job.Id, missingDep);
                    RaiseEvent(JobStatusChanged, job);
                    return;
                }

                var allDone = job.DependsOn.All(depId =>
                    _jobs.TryGetValue(depId, out var dep) && dep.Status == JobStatus.Completed);

                if (allDone) break;

                var anyFailed = job.DependsOn.Any(depId =>
                    _jobs.TryGetValue(depId, out var dep) &&
                    dep.Status is JobStatus.Failed or JobStatus.Cancelled);

                if (anyFailed)
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = "Dependency failed or was cancelled";
                    RaiseEvent(JobStatusChanged, job);
                    return;
                }

                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            RaiseEvent(JobStatusChanged, job);
            return;
        }

        try
        {
            await _workerSemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            RaiseEvent(JobStatusChanged, job);
            return;
        }

        try
        {
            // Create a fresh processor per job via factories to avoid shared mutable state
            // (stopwatches, frame counters) between concurrent jobs
            IJobProcessor? processor = null;
            lock (_processorsLock)
            {
                foreach (var factory in _processorFactories)
                {
                    var candidate = factory();
                    if (candidate is CommandLineProcessor clp)
                        clp.SetLogger(LoggerFactory);
                    if (candidate.CanProcess(job))
                    {
                        processor = candidate;
                        break;
                    }
                }
                // Fall back to shared singleton processors (for custom/third-party processors)
                processor ??= _processors.FirstOrDefault(p => p.CanProcess(job));
            }

            if (processor is null)
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = $"No processor found for job type: {job.JobType}";
                _logger.LogError("No processor found for job {JobId} (type: {JobType}, encoderId: {EncoderId})",
                    job.Id, job.JobType,
                    job is VideoJob vj ? vj.EncoderId : job is AudioJob aj ? aj.EncoderId : "N/A");
                RaiseEvent(JobStatusChanged, job);
                return;
            }

            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            _logger.LogInformation("Job started: {JobId} using {Processor}", job.Id, processor.DisplayName);
            RaiseEvent(JobStatusChanged, job);

            var progress = new Progress<JobProgress>(p =>
                RaiseEvent(JobProgressUpdated, (job, p)));

            await processor.ProcessAsync(job, progress, ct);

            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("Job completed: {JobId} in {Duration}",
                job.Id, job.CompletedAt - job.StartedAt);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Job failed: {JobId}", job.Id);
        }
        finally
        {
            _workerSemaphore.Release();
            _jobCts.TryRemove(job.Id, out _);
            RaiseEvent(JobStatusChanged, job);

            // Only cleanup temp files on success — preserve on failure for retry/debugging
            if (job.Status == JobStatus.Completed)
            {
                foreach (var file in job.FilesToCleanup)
                {
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>Safely raise an event, preventing subscriber exceptions from crashing job processing.</summary>
    private void RaiseEvent<T>(EventHandler<T>? handler, T args)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception ex) { _logger.LogWarning(ex, "Event handler threw an exception"); }
    }
}
