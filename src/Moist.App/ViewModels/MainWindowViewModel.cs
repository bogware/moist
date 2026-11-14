using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Moist.Services;
using Moist.Core.Tools;

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

    /// <summary>Tab order: Encode=0, Batch=1, Filters=2, Plugins=3, Queue=4, Settings=5.</summary>
    public const int QueueTabIndex = 4;
    public const int SettingsTabIndex = 5;

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

    /// <summary>Set once the "restored interrupted encodes" notice is on the status bar, so the
    /// concurrent tool-check can't silently overwrite it with the generic "Ready" line. Both
    /// startup tasks resume their continuations on the UI thread, so this needs no locking.</summary>
    private bool _restoreNoticePosted;

    /// <summary>
    /// Raised when required tools are missing at startup. The view subscribes
    /// to show a modal dialog. Payload is the list of missing tool statuses.
    /// </summary>
    public event EventHandler<List<ToolStatus>>? RequiredToolsMissing;

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

        // After a single-file enqueue, jump to the Queue tab so the new job is visible.
        EncodeTab.JobQueued = () => SelectedTabIndex = QueueTabIndex;

        // Wire up queue status to main status bar and window title.
        // JobQueue raises these from background job threads for every job after the first, so the
        // property writes MUST be marshaled to the UI thread — otherwise Avalonia throws a
        // cross-thread exception that JobQueue swallows, and the status bar/title freeze.
        Services.JobQueue.JobStatusChanged += (_, job) => Dispatcher.UIThread.Post(() =>
        {
            var running = Services.JobQueue.Jobs.Values.Count(j => j.Status == Core.Jobs.JobStatus.Running);
            var queued = Services.JobQueue.Jobs.Values.Count(j => j.Status == Core.Jobs.JobStatus.Queued);
            StatusText = running > 0
                ? $"Encoding: {running} running, {queued} queued"
                : queued > 0 ? $"{queued} job(s) queued" : "Ready";
            if (running == 0)
                WindowTitle = "moist";
        });

        Services.JobQueue.JobProgressUpdated += (_, args) => Dispatcher.UIThread.Post(() =>
        {
            if (args.Progress.Percentage > 0)
                WindowTitle = $"moist [{args.Progress.Percentage:F0}%] {args.Job.Name}";
        });

        // Write job logs to file when jobs complete or fail
        Services.JobQueue.JobStatusChanged += (_, job) =>
        {
            if (job.Status is Core.Jobs.JobStatus.Completed or Core.Jobs.JobStatus.Failed)
                Services.WriteJobLogToFile(job);
        };

        // Restore any interrupted jobs from previous session
        _ = RestorePersistedJobsAsync();

        // Run startup tool discovery in background
        _ = CheckToolsAtStartupAsync();
    }

    /// <summary>
    /// Put encodes from a previous session back on the queue. They are queued rather than
    /// started: coming back to a machine already grinding through work you didn't ask for
    /// would be worse than losing the queue.
    /// </summary>
    private async Task RestorePersistedJobsAsync()
    {
        try
        {
            var restorer = new EncodeQueueRestorer(Services, Services.LoggerFactory);
            var results = await restorer.RestoreAsync();
            if (results.Count == 0) return;

            var restored = results.Where(r => r.Restored).ToList();
            if (restored.Count == 0) return;

            var reusedChunks = restored.Sum(r => r.ReusedChunks);
            var chunkNote = reusedChunks > 0 ? $", reusing {reusedChunks} finished chunk(s)" : "";

            _restoreNoticePosted = true;
            StatusText = $"Restored {restored.Count} interrupted encode(s) from the previous session{chunkNote} — press Run to continue";
        }
        catch
        {
            // Restoring is a convenience; never let it stop the app starting.
        }
    }

    private async Task CheckToolsAtStartupAsync()
    {
        try
        {
            StatusText = "Checking prerequisites...";
            var results = await Services.DiscoverToolsAsync();
            var missingRequired = results
                .Where(s => s.Tool.IsRequired && !s.IsAvailable)
                .ToList();

            if (missingRequired.Count > 0)
            {
                var names = missingRequired.Select(s => s.Tool.Name).ToList();
                StartupError = $"Missing required tools: {string.Join(", ", names)}. Go to Settings to install or configure paths.";
                StatusText = "Missing prerequisites — check Settings tab";

                // Notify the view to show the missing tools dialog
                RequiredToolsMissing?.Invoke(this, missingRequired);
            }
            else
            {
                // Don't clobber a "restored interrupted encodes" notice with the generic ready
                // line — the two startup tasks race and whichever finishes last would win.
                var found = results.Count(s => s.IsAvailable);
                if (!_restoreNoticePosted)
                    StatusText = $"Ready — {found}/{results.Count} tools available";
            }
        }
        catch
        {
            if (!_restoreNoticePosted)
                StatusText = "Ready";
        }
    }

    /// <summary>
    /// Attempt to auto-install a missing required tool via ToolDiscovery.
    /// Returns the installed path, or null on failure.
    /// </summary>
    public async Task<string?> InstallToolAsync(ToolDefinition tool, IProgress<(string Status, double Percent)>? progress = null)
    {
        var path = await Services.ToolDiscovery.DownloadAndInstallAsync(tool, progress);
        if (path is not null)
        {
            // Update settings with installed path
            switch (tool.Id)
            {
                case "ffmpeg": Services.Settings.FFmpegPath = path; break;
                case "ffprobe": Services.Settings.FFprobePath = path; break;
            }
            Services.Save();
            SettingsTab.SetToolPath(tool.Id, path);

            // Re-check if all required tools are now available
            var results = await Services.DiscoverToolsAsync();
            var stillMissing = results.Where(s => s.Tool.IsRequired && !s.IsAvailable).ToList();
            if (stillMissing.Count == 0)
            {
                StartupError = string.Empty;
                var found = results.Count(s => s.IsAvailable);
                StatusText = $"Ready — {found}/{results.Count} tools available";
            }
        }
        return path;
    }
}
