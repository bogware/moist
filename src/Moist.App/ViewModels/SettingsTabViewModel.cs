using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.App.Services;

namespace Moist.App.ViewModels;

public partial class SettingsTabViewModel : ViewModelBase
{
    private readonly AppServices _services;

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

    // Status
    [ObservableProperty] private string _saveStatus = string.Empty;

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

        var s = _services.Settings;
        s.Theme = SelectedTheme;
        s.FFmpegPath = FfmpegPath.Trim();
        s.FFprobePath = FfprobePath.Trim();
        s.MkvMergePath = MkvMergePath.Trim();
        s.X264Path = X264Path.Trim();
        s.X265Path = X265Path.Trim();
        s.VapourSynthPath = VapourSynthPath.Trim();
        s.MaxConcurrentJobs = MaxConcurrentJobs;
        s.AutoStartQueue = AutoStartQueue;
        s.DeleteTempFiles = DeleteTempFiles;
        s.DefaultOutputDirectory = DefaultOutputDirectory.Trim();
        s.TempDirectory = TempDirectory.Trim();
        _services.Save();

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
