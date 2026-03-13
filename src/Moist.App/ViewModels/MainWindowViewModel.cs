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
    private string _windowTitle = "moist";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupError))]
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
            // If initialization fails (e.g. corrupt settings), delete corrupt settings and retry
            StartupError = $"Initialization warning: {ex.Message} — using defaults.";
            try
            {
                var settingsPath = Core.Settings.MoistSettings.GetSettingsPath();
                if (File.Exists(settingsPath))
                    File.Delete(settingsPath);
            }
            catch { /* best effort cleanup */ }

            Services = new AppServices();
        }

        FilterTab = new FilterTabViewModel(Services);
        EncodeTab = new EncodeTabViewModel(Services, FilterTab);
        QueueTab = new QueueTabViewModel(Services);
        BatchTab = new BatchTabViewModel(Services, EncodeTab);
        PluginManager = new PluginManagerViewModel(Services);
        SettingsTab = new SettingsTabViewModel(Services);

        // Wire up queue status to main status bar and window title
        Services.JobQueue.JobStatusChanged += (_, job) =>
        {
            var running = Services.JobQueue.Jobs.Values.Count(j => j.Status == Core.Jobs.JobStatus.Running);
            var queued = Services.JobQueue.Jobs.Values.Count(j => j.Status == Core.Jobs.JobStatus.Queued);
            StatusText = running > 0
                ? $"Encoding: {running} running, {queued} queued"
                : queued > 0 ? $"{queued} job(s) queued" : "Ready";
            if (running == 0)
                WindowTitle = "moist";
        };

        Services.JobQueue.JobProgressUpdated += (_, args) =>
        {
            if (args.Progress.Percentage > 0)
                WindowTitle = $"moist [{args.Progress.Percentage:F0}%] {args.Job.Name}";
        };

        // Run startup tool discovery in background
        _ = CheckToolsAtStartupAsync();
    }

    private async Task CheckToolsAtStartupAsync()
    {
        try
        {
            StatusText = "Checking prerequisites...";
            var results = await Services.DiscoverToolsAsync();
            var missingRequired = results
                .Where(s => s.Tool.IsRequired && !s.IsAvailable)
                .Select(s => s.Tool.Name)
                .ToList();

            if (missingRequired.Count > 0)
            {
                StartupError = $"Missing required tools: {string.Join(", ", missingRequired)}. Go to Settings to install or configure paths.";
                StatusText = "Missing prerequisites — check Settings tab";
            }
            else
            {
                var found = results.Count(s => s.IsAvailable);
                StatusText = $"Ready — {found}/{results.Count} tools available";
            }
        }
        catch
        {
            StatusText = "Ready";
        }
    }
}
