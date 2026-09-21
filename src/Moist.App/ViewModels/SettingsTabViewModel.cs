using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Services;
using Moist.Core.Tools;

namespace Moist.App.ViewModels;

public partial class SettingsTabViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private bool _hasScannedOnce;

    // Tool paths
    [ObservableProperty] private string _ffmpegPath;
    [ObservableProperty] private string _ffprobePath;
    [ObservableProperty] private string _mkvMergePath;
    [ObservableProperty] private string _x264Path;
    [ObservableProperty] private string _x265Path;
    [ObservableProperty] private string _vapourSynthPath;

    // Theme
    [ObservableProperty] private string _selectedTheme;
    public static string[] ThemeOptions { get; } = ["moist Dark", "Fluent Dark"];

    // General
    [ObservableProperty] private int _maxConcurrentJobs;
    [ObservableProperty] private bool _autoStartQueue;
    [ObservableProperty] private bool _deleteTempFiles;
    [ObservableProperty] private string _defaultOutputDirectory;
    [ObservableProperty] private string _tempDirectory;
    [ObservableProperty] private bool _promptForOutputLocation;
    [ObservableProperty] private string _defaultOutputSuffix;
    [ObservableProperty] private bool _openOutputFolderWhenDone;

    // Status
    [ObservableProperty] private string _saveStatus = string.Empty;

    // Tool discovery
    [ObservableProperty] private string _toolScanStatus = string.Empty;
    [ObservableProperty] private bool _isScanning;
    public ObservableCollection<ToolStatusViewModel> ToolStatuses { get; } = [];

    public SettingsTabViewModel(AppServices services)
    {
        _services = services;
        var s = services.Settings;

        _selectedTheme = s.Theme;
        _ffmpegPath = s.FFmpegPath;
        _ffprobePath = s.FFprobePath;
        _mkvMergePath = s.MkvMergePath;
        _x264Path = s.X264Path;
        _x265Path = s.X265Path;
        _vapourSynthPath = s.VapourSynthPath;
        _maxConcurrentJobs = s.MaxConcurrentJobs;
        _autoStartQueue = s.AutoStartQueue;
        _deleteTempFiles = s.DeleteTempFiles;
        _defaultOutputDirectory = s.DefaultOutputDirectory;
        _tempDirectory = s.TempDirectory;
        _promptForOutputLocation = s.PromptForOutputLocation;
        _defaultOutputSuffix = s.DefaultOutputSuffix;
        _openOutputFolderWhenDone = s.OpenOutputFolderWhenDone;

        // Run initial scan on construction
        _ = ScanToolsAsync();
    }

    [RelayCommand]
    private async Task ScanToolsAsync()
    {
        IsScanning = true;
        ToolScanStatus = "Scanning for tools...";

        try
        {
            // The user pressing Scan means they want a fresh look, not the cached one.
            var results = await _services.DiscoverToolsAsync(force: _hasScannedOnce);
            _hasScannedOnce = true;
            ToolStatuses.Clear();

            foreach (var status in results)
            {
                ToolStatuses.Add(new ToolStatusViewModel(status, _services, this));
            }

            // Update local path properties from settings (discovery may have updated them)
            RefreshPathsFromSettings();

            var found = results.Count(s => s.IsAvailable);
            var required = results.Where(s => s.Tool.IsRequired);
            var missingRequired = required.Where(s => !s.IsAvailable).ToList();

            if (missingRequired.Count > 0)
            {
                ToolScanStatus = $"WARNING: {string.Join(", ", missingRequired.Select(s => s.Tool.Name))} not found! These are required.";
            }
            else
            {
                ToolScanStatus = $"{found}/{results.Count} tools found. All required tools available.";
            }
        }
        catch (Exception ex)
        {
            ToolScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Re-runs the tool scan so the prerequisites panel refreshes after a tool is installed or its
    /// path is set manually. Rebuilds <see cref="ToolStatuses"/> and the required-tools summary.
    /// </summary>
    internal Task RescanAsync() => ScanToolsAsync();

    private void RefreshPathsFromSettings()
    {
        var s = _services.Settings;
        FfmpegPath = s.FFmpegPath;
        FfprobePath = s.FFprobePath;
        MkvMergePath = s.MkvMergePath;
        X264Path = s.X264Path;
        X265Path = s.X265Path;
        VapourSynthPath = s.VapourSynthPath;
    }

    [RelayCommand]
    private void Save()
    {
        // Validate
        if (MaxConcurrentJobs < 1) MaxConcurrentJobs = 1;
        if (MaxConcurrentJobs > 32) MaxConcurrentJobs = 32;

        if (string.IsNullOrWhiteSpace(FfmpegPath))
        {
            SaveStatus = "FFmpeg path cannot be empty.";
            return;
        }
        if (string.IsNullOrWhiteSpace(FfprobePath))
        {
            SaveStatus = "FFprobe path cannot be empty.";
            return;
        }

        // Validate temp directory is writable
        if (!string.IsNullOrWhiteSpace(TempDirectory))
        {
            try
            {
                Directory.CreateDirectory(TempDirectory);
                var testFile = Path.Combine(TempDirectory, $".moist_test_{Guid.NewGuid():N}");
                File.WriteAllText(testFile, "");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                SaveStatus = $"Temp directory not writable: {ex.Message}";
                return;
            }
        }

        var s = _services.Settings;
        s.Theme = SelectedTheme;
        s.FFmpegPath = FfmpegPath.Trim();
        s.FFprobePath = FfprobePath.Trim();
        s.MkvMergePath = MkvMergePath?.Trim() ?? string.Empty;
        s.X264Path = X264Path?.Trim() ?? string.Empty;
        s.X265Path = X265Path?.Trim() ?? string.Empty;
        s.VapourSynthPath = VapourSynthPath?.Trim() ?? string.Empty;
        s.MaxConcurrentJobs = MaxConcurrentJobs;
        s.AutoStartQueue = AutoStartQueue;
        s.DeleteTempFiles = DeleteTempFiles;
        s.DefaultOutputDirectory = DefaultOutputDirectory?.Trim() ?? string.Empty;
        s.TempDirectory = TempDirectory?.Trim() ?? string.Empty;
        s.PromptForOutputLocation = PromptForOutputLocation;
        s.DefaultOutputSuffix = DefaultOutputSuffix ?? string.Empty;
        s.OpenOutputFolderWhenDone = OpenOutputFolderWhenDone;

        if (!s.Save(out var saveError))
        {
            SaveStatus = $"Could not save settings: {saveError}";
            return;
        }

        // Apply max concurrent jobs change live
        _services.JobQueue.MaxConcurrentJobs = MaxConcurrentJobs;

        // Apply theme change live
        if (Avalonia.Application.Current is App app)
            app.ApplyTheme(SelectedTheme);

        SaveStatus = "Settings saved successfully.";
    }

    /// <summary>
    /// Called from code-behind when a tool path is browsed via file picker.
    /// </summary>
    public void SetToolPath(string toolName, string path)
    {
        switch (toolName)
        {
            case "ffmpeg": FfmpegPath = path; break;
            case "ffprobe": FfprobePath = path; break;
            case "mkvmerge": MkvMergePath = path; break;
            case "x264": X264Path = path; break;
            case "x265": X265Path = path; break;
            case "vspipe": VapourSynthPath = path; break;
            case "outputDir": DefaultOutputDirectory = path; break;
            case "tempDir": TempDirectory = path; break;
        }
    }
}

/// <summary>
/// ViewModel for a single tool's status in the prerequisites panel.
/// </summary>
public partial class ToolStatusViewModel : ViewModelBase
{
    private readonly ToolStatus _status;
    private readonly AppServices _services;
    private readonly SettingsTabViewModel _parent;

    [ObservableProperty] private string _statusIcon;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _actionLabel = "Install";
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private bool _isInstalling;

    public string Name => _status.Tool.Name;

    /// <summary>True when the tool is missing, so the "Browse…" button (manual locate) is offered.</summary>
    public bool IsNotAvailable => !_status.IsAvailable;

    /// <summary>
    /// True when the tool is missing and has a <see cref="ToolDefinition.HelpUrl"/> but no in-app
    /// auto-download — the panel then shows a "Get" button that opens the download page in a browser.
    /// </summary>
    public bool CanOpenPage => !_status.IsAvailable && !string.IsNullOrWhiteSpace(_status.Tool.HelpUrl);

    public ToolStatusViewModel(ToolStatus status, AppServices services, SettingsTabViewModel parent)
    {
        _status = status;
        _services = services;
        _parent = parent;

        if (status.IsAvailable)
        {
            _statusIcon = "OK";
            _statusText = status.Version ?? status.DetectedPath ?? "Found";
            _canInstall = false;
        }
        else
        {
            _statusIcon = status.Tool.IsRequired ? "!!" : "--";
            _statusText = status.Error ?? "Not found";
            _canInstall = status.Tool.DownloadUrls.Count > 0;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        IsInstalling = true;
        ActionLabel = "Installing...";
        CanInstall = false;

        try
        {
            var progress = new Progress<(string Status, double Percent)>(p =>
            {
                StatusText = p.Status;
            });

            var installedPath = await _services.ToolDiscovery.DownloadAndInstallAsync(
                _status.Tool, progress);

            if (installedPath is not null)
            {
                StatusIcon = "OK";
                StatusText = $"Installed at {installedPath}";

                // Persist the freshly-installed binary's path and update the editable Tool Paths boxes.
                ApplyPathToSettings(installedPath);
                _services.Save();
                _parent.SetToolPath(_status.Tool.Id, installedPath);

                // Re-scan so the whole panel (this row's status and the required-tools summary)
                // refreshes seamlessly without the user pressing Scan Tools.
                await _parent.RescanAsync();
            }
            else
            {
                CanInstall = true;
                ActionLabel = "Retry";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Install failed: {ex.Message}";
            CanInstall = true;
            ActionLabel = "Retry";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>
    /// Opens the tool's <see cref="ToolDefinition.HelpUrl"/> (its official download / info page) in
    /// the user's default browser. Best-effort: launching the browser can fail and is not critical.
    /// </summary>
    [RelayCommand]
    private void OpenPage()
    {
        var url = _status.Tool.HelpUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { /* Opening the browser is best-effort */ }
    }

    /// <summary>
    /// Sets this tool's path to a user-picked file (via the row's "Browse…" button), persists it,
    /// and triggers a re-scan so the panel reflects the newly-located binary immediately.
    /// </summary>
    public async Task SetManualPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        ApplyPathToSettings(path);
        _parent.SetToolPath(_status.Tool.Id, path);
        _services.Save();
        await _parent.RescanAsync();
    }

    /// <summary>
    /// Maps a resolved binary path onto the matching settings field for this tool. Shared by
    /// auto-install and manual "Browse…" so both persist to the same place.
    /// </summary>
    private void ApplyPathToSettings(string path)
    {
        switch (_status.Tool.Id)
        {
            case "ffmpeg": _services.Settings.FFmpegPath = path; break;
            case "ffprobe": _services.Settings.FFprobePath = path; break;
            case "mkvmerge": _services.Settings.MkvMergePath = path; break;
            case "x264": _services.Settings.X264Path = path; break;
            case "x265": _services.Settings.X265Path = path; break;
            case "vspipe": _services.Settings.VapourSynthPath = path; break;
            case "mp4box": _services.Settings.Mp4BoxPath = path; break;
            case "realesrgan": _services.Settings.RealEsrganPath = path; break;
            case "rife": _services.Settings.RifePath = path; break;
            case "dovi_tool": _services.Settings.DoviToolPath = path; break;
            case "hdr10plus_tool": _services.Settings.Hdr10PlusToolPath = path; break;
        }
    }
}
