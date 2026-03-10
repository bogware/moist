using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.App.Services;
using Moist.Core.Jobs;

namespace Moist.App.ViewModels;

public partial class QueueTabViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _queueStatus = "Idle";

    public QueueTabViewModel(AppServices services)
    {
        _services = services;

        _services.JobQueue.JobAdded += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Jobs.Add(new JobViewModel(job));
                QueueStatus = $"{Jobs.Count} job(s) in queue";
            });
        };

        _services.JobQueue.JobStatusChanged += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var vm = Jobs.FirstOrDefault(j => j.Id == job.Id);
                vm?.UpdateFromJob(job);
            });
        };

        _services.JobQueue.JobProgressUpdated += (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var vm = Jobs.FirstOrDefault(j => j.Id == args.Job.Id);
                vm?.UpdateProgress(args.Progress);
            });
        };
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
            Jobs.Remove(job);
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

    public JobViewModel(Job job)
    {
        Id = job.Id;
        _name = job.Name;
        _jobType = job.JobType;
        _status = job.Status.ToString();
    }

    public void UpdateFromJob(Job job)
    {
        Status = job.Status.ToString();
        if (job.ErrorMessage is not null)
            StatusLine = job.ErrorMessage;
    }

    public void UpdateProgress(JobProgress progress)
    {
        Percentage = progress.Percentage;
        Fps = progress.Fps > 0 ? $"{progress.Fps:F1} fps" : "";
        Eta = progress.Eta > TimeSpan.Zero ? $"ETA: {progress.Eta:hh\\:mm\\:ss}" : "";
        StatusLine = progress.StatusLine ?? "";
    }
}
