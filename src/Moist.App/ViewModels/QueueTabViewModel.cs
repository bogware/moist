using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Services;
using Moist.Core.Jobs;

namespace Moist.App.ViewModels;

public partial class QueueTabViewModel : ViewModelBase
{
    private readonly AppServices _services;

    /// <summary>Tails the selected job's log while the queue runs. Progress events only fire for
    /// frame-bearing video lines, so without this the log would sit static for audio/mux/vspipe
    /// jobs. Ticks on the UI thread; OutputLog is a thread-safe ConcurrentQueue.</summary>
    private readonly DispatcherTimer _logTailTimer;

    /// <summary>Last folder auto-opened for a finished encode, so a batch writing to one folder
    /// doesn't spawn a stack of Explorer windows.</summary>
    private string? _lastAutoOpenedDir;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _queueStatus = "Idle";
    [ObservableProperty] private JobViewModel? _selectedJob;
    [ObservableProperty] private string _logText = string.Empty;

    public QueueTabViewModel(AppServices services)
    {
        _services = services;

        _logTailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _logTailTimer.Tick += (_, _) =>
        {
            if (SelectedJob is not null && _services.JobQueue.Jobs.TryGetValue(SelectedJob.Id, out var job))
                RefreshLog(job);
        };

        _services.JobQueue.JobAdded += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Jobs.Add(new JobViewModel(job));
                QueueStatus = $"{Jobs.Count} job(s) in queue";

                // Give the grid a highlighted row and the log viewer something to show
                // the moment the first job lands.
                SelectedJob ??= Jobs[^1];

                // Auto-start queue if enabled and not already running
                if (_services.Settings.AutoStartQueue && !IsRunning)
                    StartQueueCommand.Execute(null);
            });
        };

        _services.JobQueue.JobStatusChanged += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var vm = Jobs.FirstOrDefault(j => j.Id == job.Id);
                vm?.UpdateFromJob(job);

                // When a job starts running, surface its live log automatically — unless the
                // user is actively watching a different unfinished job — so "Start Queue"
                // shows streaming output instead of an empty pane.
                if (job.Status == JobStatus.Running && vm is not null
                    && (SelectedJob is null || SelectedJob.Status is "Completed" or "Failed" or "Cancelled"))
                    SelectedJob = vm;

                // Refresh log whenever job status changes (start, fail, complete)
                // so error output is visible even when no progress events fired
                if (SelectedJob is not null && job.Id == SelectedJob.Id)
                    RefreshLog(job);

                if (job.Status == JobStatus.Completed && _services.Settings.OpenOutputFolderWhenDone)
                    TryAutoOpenOutputFolder(job);

                MaybeMarkBatchComplete();
            });
        };

        _services.JobQueue.JobProgressUpdated += (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var vm = Jobs.FirstOrDefault(j => j.Id == args.Job.Id);
                vm?.UpdateProgress(args.Progress);

                // Refresh log if this is the selected job
                if (SelectedJob is not null && args.Job.Id == SelectedJob.Id)
                    RefreshLog(args.Job);
            });
        };
    }

    partial void OnIsRunningChanged(bool value)
    {
        if (value) _logTailTimer.Start();
        else _logTailTimer.Stop();
    }

    partial void OnSelectedJobChanged(JobViewModel? value)
    {
        if (value is null)
        {
            LogText = string.Empty;
            return;
        }
        if (_services.JobQueue.Jobs.TryGetValue(value.Id, out var job))
            RefreshLog(job);
    }

    private void RefreshLog(Job job)
    {
        LogText = string.Join('\n', job.OutputLog.Where(l => !IsProgressKeyValue(l)));
    }

    /// <summary>
    /// FFmpeg's machine-readable <c>-progress pipe:1</c> output ("frame=120", "out_time=00:00:05.0",
    /// "progress=continue"). It's the heartbeat the stall watchdog and the progress bar run on, but
    /// to a person it's noise that buries the real messages. Hidden here only; job log files keep it.
    /// </summary>
    internal static bool IsProgressKeyValue(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0 || line.IndexOf(' ') is >= 0 and var sp && sp < eq) return false;
        var key = line[..eq];
        return key is "frame" or "fps" or "bitrate" or "total_size" or "out_time_us" or "out_time_ms"
                   or "out_time" or "dup_frames" or "drop_frames" or "speed" or "progress"
               || key.StartsWith("stream_", StringComparison.Ordinal);
    }

    [RelayCommand]
    private void StartQueue()
    {
        if (IsRunning) return;

        _lastAutoOpenedDir = null;
        IsRunning = true;
        QueueStatus = "Running...";

        // JobQueue.StartAsync only returns when the queue is Stop()ped — its dispatcher loop runs
        // until the cancellation token fires. Awaiting it as though it signalled batch completion
        // left QueueStatus stuck on "Running..." and IsRunning stuck true until the user pressed
        // Stop. Fire it and forget; completion is detected in the JobStatusChanged handler
        // (MaybeMarkBatchComplete). The continuation resumes on the UI thread, so writing
        // QueueStatus there is safe.
        _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        try
        {
            await _services.JobQueue.StartAsync();
        }
        catch (Exception ex)
        {
            QueueStatus = $"Error: {ex.Message}";
            IsRunning = false;
        }
    }

    /// <summary>
    /// StartAsync never signals that a batch finished (it only unwinds on Stop), so completion is
    /// inferred here: once the queue is running and nothing is still Running or Queued, the batch
    /// is done. Runs on the UI thread from the JobStatusChanged handler.
    /// </summary>
    private void MaybeMarkBatchComplete()
    {
        if (!IsRunning) return;

        var jobs = _services.JobQueue.Jobs.Values;
        if (!jobs.Any()) return;
        if (jobs.Any(j => j.Status is JobStatus.Running or JobStatus.Queued)) return;

        IsRunning = false;
        var failed = jobs.Count(j => j.Status == JobStatus.Failed);
        QueueStatus = failed > 0 ? $"Completed with {failed} failure(s)" : "Completed";
    }

    [RelayCommand]
    private void StopQueue()
    {
        _services.JobQueue.Stop();
        IsRunning = false;
        QueueStatus = "Stopped";
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var completed = Jobs.Where(j => j.Status is "Completed" or "Failed" or "Cancelled").ToList();
        foreach (var job in completed)
        {
            _services.JobQueue.Remove(job.Id);
            Jobs.Remove(job);
        }
    }

    [RelayCommand]
    private void RemoveJob(JobViewModel? job)
    {
        if (job is null) return;
        _services.JobQueue.Remove(job.Id);
        Jobs.Remove(job);
        QueueStatus = $"{Jobs.Count} job(s) in queue";
    }

    [RelayCommand]
    private void CancelJob(JobViewModel? job)
    {
        if (job is null || job.Status is not ("Running" or "Queued")) return;
        _services.JobQueue.CancelJob(job.Id);
    }

    [RelayCommand]
    private void OpenOutputFile(JobViewModel? job)
    {
        if (job is null) return;
        if (!_services.JobQueue.Jobs.TryGetValue(job.Id, out var realJob)) return;
        if (!File.Exists(realJob.OutputFile)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = realJob.OutputFile,
                UseShellExecute = true,
            });
        }
        catch { /* best effort */ }
    }

    [RelayCommand]
    private void OpenOutputFolder(JobViewModel? job)
    {
        if (job is null) return;
        if (!_services.JobQueue.Jobs.TryGetValue(job.Id, out var realJob)) return;

        var dir = Path.GetDirectoryName(realJob.OutputFile);
        if (dir is null || !Directory.Exists(dir)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Honors the "open output folder when done" setting. Fires only for the final output —
    /// jobs whose OutputFile lives outside the temp directory (the video/audio/chunk
    /// intermediates all write into TempDirectory) — and skips a folder already opened this run.
    /// </summary>
    private void TryAutoOpenOutputFolder(Job job)
    {
        if (IsTempPath(job.OutputFile) || !File.Exists(job.OutputFile)) return;

        var dir = Path.GetDirectoryName(job.OutputFile);
        if (string.IsNullOrEmpty(dir) || dir == _lastAutoOpenedDir || !Directory.Exists(dir)) return;
        _lastAutoOpenedDir = dir;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private bool IsTempPath(string path)
    {
        var temp = _services.Settings.TempDirectory;
        if (string.IsNullOrEmpty(temp) || string.IsNullOrEmpty(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(temp);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public partial class JobViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _jobType;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private string _status;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndeterminate))]
    private double _percentage;
    [ObservableProperty] private string _fps = "";
    [ObservableProperty] private string _eta = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _outputFile = "";
    [ObservableProperty] private string _estimatedSize = "";

    public bool IsCompleted => Status == "Completed";

    /// <summary>Still cancellable: queued, waiting on a dependency, or running.</summary>
    public bool IsActive => Status is "Pending" or "Queued" or "Running";

    /// <summary>Running but with no measurable percentage yet (unknown frame count, or the brief
    /// audio/mux phase) — drives an indeterminate progress bar so the row still shows motion.</summary>
    public bool IsIndeterminate => Status == "Running" && Percentage <= 0;

    public JobViewModel(Job job)
    {
        Id = job.Id;
        _name = job.Name;
        _jobType = job.JobType;
        _status = job.Status.ToString();
        _outputFile = job.OutputFile;
    }

    public void UpdateFromJob(Job job)
    {
        Status = job.Status.ToString();
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsActive));

        // Some jobs (the mux, a stream copy) finish without ever reporting a percentage; a
        // completed job's bar should read full, not empty.
        if (job.Status == JobStatus.Completed)
        {
            Percentage = 100;
            Eta = "";
        }
        if (job.ErrorMessage is not null)
            StatusLine = job.ErrorMessage;

        // Update estimated size from output file when completed
        if (job.Status == JobStatus.Completed && File.Exists(job.OutputFile))
        {
            var size = new FileInfo(job.OutputFile).Length;
            EstimatedSize = FormatFileSize(size);
        }
    }

    public void UpdateProgress(JobProgress progress)
    {
        Percentage = progress.Percentage;
        Fps = progress.Fps > 0 ? $"{progress.Fps:F1} fps" : "";
        Eta = progress.Eta > TimeSpan.Zero ? $"ETA: {progress.Eta:hh\\:mm\\:ss}" : "";
        StatusLine = progress.StatusLine ?? "";

        // Estimate output size from current progress data
        if (progress.OutputSizeBytes > 0 && progress.Percentage > 5)
        {
            var estimated = (long)(progress.OutputSizeBytes / (progress.Percentage / 100.0));
            EstimatedSize = $"~{FormatFileSize(estimated)}";
        }
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
