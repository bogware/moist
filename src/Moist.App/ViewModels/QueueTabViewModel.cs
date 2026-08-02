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

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _queueStatus = "Idle";
    [ObservableProperty] private JobViewModel? _selectedJob;
    [ObservableProperty] private string _logText = string.Empty;

    public QueueTabViewModel(AppServices services)
    {
        _services = services;

        _services.JobQueue.JobAdded += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Jobs.Add(new JobViewModel(job));
                QueueStatus = $"{Jobs.Count} job(s) in queue";

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

                // Refresh log whenever job status changes (start, fail, complete)
                // so error output is visible even when no progress events fired
                if (SelectedJob is not null && job.Id == SelectedJob.Id)
                    RefreshLog(job);
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
        LogText = string.Join('\n', job.OutputLog);
    }

    [RelayCommand]
    private async Task StartQueue()
    {
        IsRunning = true;
        QueueStatus = "Running...";
        try
        {
            await _services.JobQueue.StartAsync();
            QueueStatus = "Completed";
        }
        catch (Exception ex)
        {
            QueueStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
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
}

public partial class JobViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _jobType;
    [ObservableProperty] private string _status;
    [ObservableProperty] private double _percentage;
    [ObservableProperty] private string _fps = "";
    [ObservableProperty] private string _eta = "";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private string _outputFile = "";
    [ObservableProperty] private string _estimatedSize = "";

    public bool IsCompleted => Status == "Completed";

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
