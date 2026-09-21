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
    // Per-job dispatch guard: a job id is added here the first time it is processed,
    // so it can never be launched twice (e.g. startup snapshot racing the dispatcher).
    private readonly ConcurrentDictionary<string, byte> _dispatched = new();
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
    // Permits owed to a concurrency reduction that could not be applied immediately
    // (all permits were in use). Absorbed by ReleaseWorkerPermit() as jobs finish.
    private int _pendingPermitReduction;
    public int MaxConcurrentJobs
    {
        get => _maxConcurrentJobs;
        set
        {
            if (value < 1) value = 1;
            if (value == _maxConcurrentJobs) return;
            var old = _maxConcurrentJobs;
            _maxConcurrentJobs = value;

            // Keep the semaphore's live permit count in step with the limit in BOTH
            // directions, and never over-release. A plain SemaphoreSlim has no way to
            // shrink, so a decrease drains free permits now and defers the rest to job
            // completions via _pendingPermitReduction; an increase cancels any still-
            // pending reduction before releasing the remainder.
            if (value > old)
            {
                var inc = value - old;
                var cancel = Math.Min(inc, Volatile.Read(ref _pendingPermitReduction));
                if (cancel > 0) Interlocked.Add(ref _pendingPermitReduction, -cancel);
                if (inc - cancel > 0) _workerSemaphore.Release(inc - cancel);
            }
            else
            {
                var dec = old - value;
                for (var i = 0; i < dec; i++)
                {
                    // Take a free permit outright if one is available; otherwise defer
                    // the reduction so the next completing job absorbs its permit
                    // instead of releasing it. In-flight jobs are never interrupted.
                    if (_workerSemaphore.Wait(0)) continue;
                    Interlocked.Increment(ref _pendingPermitReduction);
                }
            }
            _logger.LogInformation("MaxConcurrentJobs changed from {Old} to {New}", old, value);
        }
    }

    // Retention cap for settled (Completed/Failed/Cancelled) jobs. 0 or negative = unlimited.
    // Default is unlimited so existing behaviour and tests are unchanged; a long-lived host
    // (e.g. `moist serve`) sets a positive cap so _jobs and the per-job OutputLog buffers do
    // not grow without bound.
    private volatile int _maxCompletedJobsRetained;
    private readonly object _retentionLock = new();

    /// <summary>
    /// Maximum number of settled jobs (<see cref="JobStatus.Completed"/>, <see cref="JobStatus.Failed"/>
    /// or <see cref="JobStatus.Cancelled"/>) to keep in the queue. Once the number of settled jobs
    /// exceeds this cap, the oldest settled jobs are evicted from the queue and their retained output
    /// logs dropped, so a long-lived queue stays bounded. <c>0</c> or negative (the default) means
    /// unlimited — nothing is ever evicted, preserving the original behaviour. Running and queued jobs
    /// are never evicted, nor is any settled job that an unfinished job still depends on.
    /// </summary>
    public int MaxCompletedJobsRetained
    {
        get => _maxCompletedJobsRetained;
        set => _maxCompletedJobsRetained = value;
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

    /// <summary>
    /// Resolve the processor that would handle a job, using the exact same order and rules as the
    /// live queue (registration-order factories first, first CanProcess wins, then singletons).
    /// Returns a fresh instance (factories create per-job state), or null if nothing matches.
    /// Lets callers such as the snippet-preview service run one job through the real processor
    /// without enqueuing it, so the preview reflects exactly what the encode would produce.
    /// </summary>
    public IJobProcessor? ResolveProcessor(Job job)
    {
        lock (_processorsLock)
        {
            foreach (var factory in _processorFactories)
            {
                var candidate = factory();
                if (candidate is CommandLineProcessor clp)
                    clp.SetLogger(LoggerFactory);
                if (candidate.CanProcess(job))
                    return candidate;
            }
            return _processors.FirstOrDefault(p => p.CanProcess(job));
        }
    }

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
        // Allow a legitimately re-added id to be dispatched again.
        _dispatched.TryRemove(jobId, out _);
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
            RaiseStatusChanged(job);
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
        // A cancelled encode is not guaranteed to unwind within the timeout (killing a
        // process tree plus OnAfterSuccess/finally can take longer). Only dispose the
        // semaphores once the running task has actually finished, so a slow-unwinding
        // job never calls Release() on a disposed semaphore. The bounded leak of the
        // two semaphores until process exit is acceptable.
        var finished = _runningTask?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        _cts?.Dispose();
        _cts = null;
        if (finished)
        {
            _workerSemaphore.Dispose();
            _pendingSignal.Dispose();
        }

        // Dispose all per-job CTS
        foreach (var cts in _jobCts.Values)
            cts.Dispose();
        _jobCts.Clear();
    }

    private async Task ProcessJobAsync(Job job, CancellationToken queueCt)
    {
        // Atomic per-job dispatch guard: a job can never be launched twice, whether by
        // the startup snapshot racing the dispatcher (Enqueue during StartAsync) or any
        // future double-enqueue. The entry is cleared in Remove() so a re-added id runs.
        if (!_dispatched.TryAdd(job.Id, 0))
            return;

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
                    RaiseStatusChanged(job);
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
                    RaiseStatusChanged(job);
                    return;
                }

                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            RaiseStatusChanged(job);
            return;
        }

        try
        {
            await _workerSemaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            RaiseStatusChanged(job);
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
                RaiseStatusChanged(job);
                return;
            }

            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            _logger.LogInformation("Job started: {JobId} using {Processor}", job.Id, processor.DisplayName);
            RaiseStatusChanged(job);

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
            job.AppendLog($"--- JOB FAILED ---");
            job.AppendLog(ex.Message);
            _logger.LogError(ex, "Job failed: {JobId}", job.Id);
        }
        finally
        {
            ReleaseWorkerPermit();
            _jobCts.TryRemove(job.Id, out _);
            RaiseStatusChanged(job);

            // Only cleanup temp files on success — preserve on failure for retry/debugging
            if (job.Status == JobStatus.Completed)
            {
                foreach (var file in job.FilesToCleanup)
                {
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch { /* best effort */ }
                }

                // Remove any parent directory the cleanup just emptied — otherwise a per-run
                // chunk directory (chunks/<hash>/) is left behind empty after every chunked
                // encode and they accumulate under the temp tree. Only delete it when actually
                // empty, so nothing shared is ever removed.
                foreach (var dir in job.FilesToCleanup
                             .Select(Path.GetDirectoryName)
                             .Where(d => !string.IsNullOrEmpty(d))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir!).Any())
                            Directory.Delete(dir!);
                    }
                    catch { /* best effort */ }
                }
            }
        }
    }

    /// <summary>
    /// Return a worker permit when a job finishes. If a concurrency reduction is pending,
    /// the permit is absorbed toward it instead of being released, so lowering
    /// MaxConcurrentJobs actually shrinks the live pool. Tolerates a disposed semaphore
    /// (a job may still be unwinding after Dispose's wait timed out).
    /// </summary>
    private void ReleaseWorkerPermit()
    {
        if (TryAbsorbPermitReduction())
            return;

        try
        {
            _workerSemaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // Queue was disposed while this job was still unwinding — nothing to release.
        }
    }

    /// <summary>
    /// Atomically decrement _pendingPermitReduction if it is positive. Returns true when a
    /// pending reduction was consumed (the caller must NOT release its permit). Never lets
    /// the counter go negative under concurrent completions.
    /// </summary>
    private bool TryAbsorbPermitReduction()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _pendingPermitReduction);
            if (current <= 0) return false;
        }
        while (Interlocked.CompareExchange(ref _pendingPermitReduction, current - 1, current) != current);
        return true;
    }

    /// <summary>
    /// Raise <see cref="JobStatusChanged"/> and, when the job has reached a terminal state, enforce
    /// the retention cap. Routing every status change through here means each place a job settles —
    /// completion, failure, cancellation, a missing or failed dependency — bounds memory identically.
    /// </summary>
    private void RaiseStatusChanged(Job job)
    {
        RaiseEvent(JobStatusChanged, job);
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            EnforceRetentionPolicy();
    }

    /// <summary>
    /// Evict the oldest settled jobs when more than <see cref="MaxCompletedJobsRetained"/> are
    /// retained, dropping their <see cref="Job.OutputLog"/> buffers so a long-lived queue stays
    /// bounded. Never touches a running or queued job, and never evicts a settled job that an
    /// unfinished job still depends on (its result may still be needed).
    /// </summary>
    private void EnforceRetentionPolicy()
    {
        if (_maxCompletedJobsRetained <= 0) return; // unlimited — nothing to do

        // Serialize eviction so concurrent completions don't each remove past the cap.
        lock (_retentionLock)
        {
            var cap = _maxCompletedJobsRetained;
            if (cap <= 0) return;

            static bool IsSettled(Job j) =>
                j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

            var settled = _jobs.Values
                .Where(IsSettled)
                .OrderBy(j => j.CompletedAt ?? j.CreatedAt) // oldest first; CompletedAt is unset on failure
                .ToList();

            var excess = settled.Count - cap;
            if (excess <= 0) return;

            // Ids still needed by a job that hasn't finished — evicting one would strand the dependant.
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var j in _jobs.Values)
            {
                if (IsSettled(j)) continue;
                foreach (var dep in j.DependsOn)
                    pinned.Add(dep);
            }

            foreach (var job in settled)
            {
                if (excess <= 0) break;
                if (pinned.Contains(job.Id)) continue;
                if (_jobs.TryRemove(job.Id, out var removed))
                {
                    // Drop the dispatch guard and the retained log text for the evicted job.
                    _dispatched.TryRemove(job.Id, out _);
                    _jobCts.TryRemove(job.Id, out _);
                    removed.OutputLog.Clear();
                    excess--;
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
