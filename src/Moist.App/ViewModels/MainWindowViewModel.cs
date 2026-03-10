using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Moist.App.Services;

namespace Moist.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public AppServices Services { get; }
    public EncodeTabViewModel EncodeTab { get; }
    public QueueTabViewModel QueueTab { get; }
    public FilterTabViewModel FilterTab { get; }
    public BatchTabViewModel BatchTab { get; }
    public PluginManagerViewModel PluginManager { get; }
    public SettingsTabViewModel SettingsTab { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _startupError = string.Empty;

    public bool HasStartupError => !string.IsNullOrEmpty(StartupError);

    public MainWindowViewModel()
    {
        try
        {
            Services = new AppServices();
        }
        catch (Exception ex)
        {
            // If initialization fails (e.g. corrupt settings), log and try fresh
            StartupError = $"Initialization warning: {ex.Message} — using defaults.";
            try { Services = new AppServices(); }
            catch { Services = new AppServices(); }
        }

        FilterTab = new FilterTabViewModel(Services);
        EncodeTab = new EncodeTabViewModel(Services, FilterTab);
        QueueTab = new QueueTabViewModel(Services);
        BatchTab = new BatchTabViewModel(Services, EncodeTab);
        PluginManager = new PluginManagerViewModel(Services);
        SettingsTab = new SettingsTabViewModel(Services);

        // Wire up queue status to main status bar
        Services.JobQueue.JobStatusChanged += (_, job) =>
        {
            var running = Services.JobQueue.Jobs.Values.Count(j => j.Status == Core.Jobs.JobStatus.Running);
            var queued = Services.JobQueue.Jobs.Values.Count(j => j.Status == Core.Jobs.JobStatus.Queued);
            StatusText = running > 0
                ? $"Encoding: {running} running, {queued} queued"
                : queued > 0 ? $"{queued} job(s) queued" : "Ready";
        };
    }
}
