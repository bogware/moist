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

    /// <summary>Real-ESRGAN ncnn-vulkan binary for the deep-model AI upscale. Optional; the AI
    /// profile refuses to run without it, and points at Settings / auto-download.</summary>
    public string RealEsrganPath { get; set; } = "realesrgan-ncnn-vulkan";

    /// <summary>Dolby Vision RPU tooling. Optional; without it a re-encode drops the RPU.</summary>
    public string DoviToolPath { get; set; } = "dovi_tool";

    /// <summary>HDR10+ dynamic metadata tooling. Optional; without it a re-encode drops it.</summary>
    public string Hdr10PlusToolPath { get; set; } = "hdr10plus_tool";

    // Job queue
    public int MaxConcurrentJobs { get; set; } = 1;
    public bool AutoStartQueue { get; set; }
    public bool DeleteTempFiles { get; set; } = true;

    // UI
    public string Theme { get; set; } = "moist Dark";
    public double WindowWidth { get; set; } = DefaultWindowWidth;
    public double WindowHeight { get; set; } = DefaultWindowHeight;

    /// <summary>Saved window position. Null means "let the OS decide" — never NaN, which System.Text.Json cannot serialize.</summary>
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    public const double DefaultWindowWidth = 1200;
    public const double DefaultWindowHeight = 800;

    // Defaults
    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public string DefaultVideoEncoder { get; set; } = "ffmpeg-x265";
    public string DefaultAudioEncoder { get; set; } = "ffmpeg-audio-opus";
    public string DefaultContainer { get; set; } = "Mkv";

    /// <summary>
    /// When true, the GUI leaves the output path blank so the user is asked where to save each
    /// file (via Browse). When false, it auto-fills using <see cref="DefaultOutputDirectory"/>
    /// (falling back to the source folder) plus <see cref="DefaultOutputSuffix"/>.
    /// </summary>
    public bool PromptForOutputLocation { get; set; }

    /// <summary>
    /// Appended to an auto-generated output filename, before the extension. Empty is allowed
    /// (write beside the source with the same stem — only safe when the container differs or a
    /// default output directory is set, so the input isn't overwritten).
    /// </summary>
    public string DefaultOutputSuffix { get; set; } = "_encoded";

    /// <summary>Reveal the destination folder in the file manager when a job finishes.</summary>
    public bool OpenOutputFolderWhenDone { get; set; }

    // Temp
    public string TempDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "moist");

    /// <summary>
    /// Repair anything unusable that was loaded from disk (or left behind by an older
    /// build) so a bad stored value can't wedge every encode.
    /// </summary>
    public MoistSettings Repaired()
    {
        Sanitize();
        return this;
    }

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

    /// <summary>
    /// Persist settings to disk. Never throws — a failure here must not be able to
    /// take down the app on shutdown. Returns false (with the reason in <paramref name="error"/>)
    /// so callers that can surface a message to the user are able to.
    /// </summary>
    public bool Save(out string? error)
    {
        error = null;
        try
        {
            Sanitize();
            var path = GetSettingsPath();
            var json = JsonSerializer.Serialize(this, JsonOptions);

            // Write to a temp file first so an interrupted write can't corrupt the real one.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Persist settings, ignoring any failure.</summary>
    public bool Save() => Save(out _);

    /// <summary>
    /// Coerce values that cannot be represented in JSON (NaN / Infinity) back to sane defaults.
    /// Window geometry comes from the UI toolkit, which can hand us non-finite values.
    /// </summary>
    private void Sanitize()
    {
        if (!double.IsFinite(WindowWidth) || WindowWidth <= 0) WindowWidth = DefaultWindowWidth;
        if (!double.IsFinite(WindowHeight) || WindowHeight <= 0) WindowHeight = DefaultWindowHeight;
        if (WindowX is not null && !double.IsFinite(WindowX.Value)) WindowX = null;
        if (WindowY is not null && !double.IsFinite(WindowY.Value)) WindowY = null;
        if (MaxConcurrentJobs < 1) MaxConcurrentJobs = 1;
        if (MaxConcurrentJobs > 32) MaxConcurrentJobs = 32;
        if (!IsUsablePath(TempDirectory)) TempDirectory = DefaultTempDirectory;

        // A suffix carrying a path separator or other invalid filename char would build a
        // broken output path — strip anything that can't appear in a filename.
        DefaultOutputSuffix ??= string.Empty;
        foreach (var c in Path.GetInvalidFileNameChars())
            if (DefaultOutputSuffix.Contains(c)) DefaultOutputSuffix = DefaultOutputSuffix.Replace(c.ToString(), "");
    }

    public static string DefaultTempDirectory => Path.Combine(Path.GetTempPath(), "moist");

    /// <summary>
    /// A temp directory that can't be created makes every encode fail, and the bad value
    /// gets written straight back on the next save. Reject it before it can persist.
    /// </summary>
    private static bool IsUsablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        try
        {
            _ = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static MoistSettings Load()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
            return new MoistSettings();

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<MoistSettings>(json, JsonOptions) ?? new MoistSettings();
            return loaded.Repaired();
        }
        catch
        {
            // Corrupt or unreadable settings file — start fresh with defaults
            return new MoistSettings();
        }
    }
}
