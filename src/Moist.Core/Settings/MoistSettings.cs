using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moist.Core.Settings;

/// <summary>
/// Global application settings, persisted as JSON.
/// Replaces MeGUI's XML-based MeGUISettings with a cleaner approach.
/// </summary>
public sealed class MoistSettings
{
    // Tool paths
    public string FFmpegPath { get; set; } = "ffmpeg";
    public string FFprobePath { get; set; } = "ffprobe";
    public string MkvMergePath { get; set; } = "mkvmerge";
    public string Mp4BoxPath { get; set; } = "mp4box";
    public string X264Path { get; set; } = "x264";
    public string X265Path { get; set; } = "x265";
    public string OpusEncPath { get; set; } = "opusenc";
    public string VapourSynthPath { get; set; } = "vspipe";
    public string AviSynthPath { get; set; } = "avs2pipemod";

    // Job queue
    public int MaxConcurrentJobs { get; set; } = 1;
    public bool AutoStartQueue { get; set; }
    public bool DeleteTempFiles { get; set; } = true;

    // UI
    public string Theme { get; set; } = "moist Dark";
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    // Defaults
    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public string DefaultVideoEncoder { get; set; } = "ffmpeg-x265";
    public string DefaultAudioEncoder { get; set; } = "ffmpeg-audio-opus";
    public string DefaultContainer { get; set; } = "Mkv";

    // Temp
    public string TempDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "moist");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetSettingsPath()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "moist");
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "settings.json");
    }

    public void Save()
    {
        var path = GetSettingsPath();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static MoistSettings Load()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
            return new MoistSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MoistSettings>(json, JsonOptions) ?? new MoistSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file — start fresh with defaults
            return new MoistSettings();
        }
    }
}
