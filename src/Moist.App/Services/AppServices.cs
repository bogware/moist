using Microsoft.Extensions.Logging;
using Moist.Core.Encoders;
using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Moist.Core.Profiles;
using Moist.Core.Settings;
using Moist.Core.Tools;
using Moist.Filters;
using Moist.Filters.Engines;

namespace Moist.App.Services;

/// <summary>
/// Central service container. Initializes all core systems and wires them together.
/// </summary>
public sealed class AppServices : IDisposable
{
    public MoistSettings Settings { get; }
    public EncoderRegistry Encoders { get; }
    public FilterRegistry Filters { get; }
    public JobQueue JobQueue { get; }
    public ProfileManager ProfileManager { get; }
    public PluginSourceManager PluginSources { get; }
    public FilterPresetManager FilterPresets { get; }
    public ILoggerFactory LoggerFactory { get; }
    public ToolDiscovery ToolDiscovery { get; }

    /// <summary>Media analyzer — uses current FFprobePath from settings. Recreated only if path changes.</summary>
    public FFprobeMediaAnalyzer MediaAnalyzer
    {
        get
        {
            if (_mediaAnalyzer is null || _lastFfprobePath != Settings.FFprobePath)
            {
                _lastFfprobePath = Settings.FFprobePath;
                _mediaAnalyzer = new FFprobeMediaAnalyzer(Settings.FFprobePath);
            }
            return _mediaAnalyzer;
        }
    }
    private FFprobeMediaAnalyzer? _mediaAnalyzer;
    private string _lastFfprobePath = string.Empty;

    /// <summary>Preview service — uses current FFmpegPath from settings. Recreated only if path changes.</summary>
    public VideoPreviewService PreviewService
    {
        get
        {
            if (_previewService is null || _lastFfmpegPathPreview != Settings.FFmpegPath)
            {
                _lastFfmpegPathPreview = Settings.FFmpegPath;
                _previewService = new VideoPreviewService(Settings.FFmpegPath);
            }
            return _previewService;
        }
    }
    private VideoPreviewService? _previewService;
    private string _lastFfmpegPathPreview = string.Empty;

    public AppServices()
    {
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Debug)
                .AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                    options.SingleLine = true;
                });
        });

        var logger = LoggerFactory.CreateLogger<AppServices>();
        logger.LogInformation("moist starting up — initializing services");

        Settings = MoistSettings.Load();
        Encoders = new EncoderRegistry();
        Filters = new FilterRegistry();
        JobQueue = new JobQueue(Settings.MaxConcurrentJobs, LoggerFactory);
        ProfileManager = new ProfileManager();
        PluginSources = new PluginSourceManager();
        FilterPresets = new FilterPresetManager();
        ToolDiscovery = new ToolDiscovery(LoggerFactory);

        RegisterDefaultEncoders();
        RegisterDefaultFilters();
        RegisterDefaultProcessors();
    }

    private void RegisterDefaultEncoders()
    {
        // Video encoders via FFmpeg
        RegisterVideoEncoder("ffmpeg-x264", "x264 via FFmpeg (H.264)", "x264",
            typeof(X264Settings), "H.264 encoding via FFmpeg's libx264");
        RegisterVideoEncoder("ffmpeg-x265", "x265 via FFmpeg (H.265)", "x265",
            typeof(X265Settings), "H.265 encoding via FFmpeg's libx265");
        RegisterVideoEncoder("ffmpeg-svtav1", "SVT-AV1 via FFmpeg", "svtav1",
            typeof(SvtAv1Settings), "AV1 encoding via FFmpeg's libsvtav1");
        RegisterVideoEncoder("ffmpeg-vp9", "VP9 via FFmpeg", "vp9",
            typeof(VideoEncoderSettings), "VP9 encoding via FFmpeg's libvpx-vp9");
        RegisterVideoEncoder("ffmpeg-ffv1", "FFV1 Lossless via FFmpeg", "ffv1",
            typeof(VideoEncoderSettings), "Lossless archival codec via FFmpeg");

        // Standalone CLI encoders
        Encoders.Register(new EncoderInfo
        {
            Id = "cli-x264",
            Name = "x264 (Standalone CLI)",
            Category = "Video",
            SettingsType = typeof(X264Settings),
            Description = "Direct x264 CLI for maximum control. Piped from FFmpeg.",
        }, () => new X264CliProcessor(Settings.X264Path, Settings.FFmpegPath));

        Encoders.Register(new EncoderInfo
        {
            Id = "cli-x265",
            Name = "x265 (Standalone CLI)",
            Category = "Video",
            SettingsType = typeof(X265Settings),
            Description = "Direct x265 CLI for maximum control. Piped from FFmpeg.",
        }, () => new X265CliProcessor(Settings.X265Path, Settings.FFmpegPath));

        // Audio encoders via FFmpeg
        RegisterAudioEncoder("ffmpeg-audio-opus", "Opus", "opus",
            typeof(OpusSettings), "Best general-purpose lossy audio codec");
        RegisterAudioEncoder("ffmpeg-audio-aac", "AAC", "aac",
            typeof(AacSettings), "Advanced Audio Coding — wide compatibility");
        RegisterAudioEncoder("ffmpeg-audio-mp3", "MP3 (LAME)", "mp3",
            typeof(Mp3Settings), "LAME MP3 encoder with VBR quality scale");
        RegisterAudioEncoder("ffmpeg-audio-flac", "FLAC (Lossless)", "flac",
            typeof(FlacSettings), "Lossless audio compression");
        RegisterAudioEncoder("ffmpeg-audio-ac3", "AC3 (Dolby Digital)", "ac3",
            typeof(Ac3Settings), "Dolby Digital 5.1 surround audio");
        RegisterAudioEncoder("ffmpeg-audio-eac3", "E-AC3 (Dolby Digital+)", "eac3",
            typeof(Eac3Settings), "Enhanced Dolby Digital Plus surround");
        RegisterAudioEncoder("ffmpeg-audio-vorbis", "Vorbis", "vorbis",
            typeof(VorbisSettings), "Ogg Vorbis audio for WebM containers");
        RegisterAudioEncoder("ffmpeg-audio-copy", "Passthrough (Copy)", "copy",
            typeof(AudioEncoderSettings), "Copy audio without re-encoding");

        // Muxer
        Encoders.Register(new EncoderInfo
        {
            Id = "ffmpeg-mux",
            Name = "FFmpeg Muxer",
            Category = "Muxer",
            SettingsType = typeof(object),
            Description = "Universal muxer via FFmpeg",
            OutputFormats = ["mkv", "mp4", "webm", "avi", "ts", "mov"],
        }, () => new FFmpegMuxProcessor(Settings.FFmpegPath));
    }

    private void RegisterVideoEncoder(string id, string name, string codec, Type settingsType, string desc)
    {
        Encoders.Register(new EncoderInfo
        {
            Id = id,
            Name = name,
            Category = "Video",
            SettingsType = settingsType,
            Description = desc,
        }, () => new FFmpegVideoProcessor(Settings.FFmpegPath, codec));
    }

    private void RegisterAudioEncoder(string id, string name, string codec, Type settingsType, string desc)
    {
        Encoders.Register(new EncoderInfo
        {
            Id = id,
            Name = name,
            Category = "Audio",
            SettingsType = settingsType,
            Description = desc,
        }, () => new FFmpegAudioProcessor(Settings.FFmpegPath, codec));
    }

    private void RegisterDefaultFilters()
    {
        Filters.Register(new FFmpegFilterEngine());
        Filters.Register(new VapourSynthFilterEngine(Settings.VapourSynthPath));
        Filters.Register(new AviSynthFilterEngine());
    }

    private void RegisterDefaultProcessors()
    {
        foreach (var encoder in Encoders.Encoders.Values)
        {
            var encoderId = encoder.Id; // capture for closure

            // Use factories to create fresh processor instances per job,
            // avoiding shared mutable state (stopwatches, counters) between concurrent jobs
            if (encoder.Category == "Video")
            {
                JobQueue.RegisterProcessorFactory(() =>
                    new TwoPassProcessor(Encoders.CreateProcessor(encoderId)));
            }

            JobQueue.RegisterProcessorFactory(() => Encoders.CreateProcessor(encoderId));
        }
    }

    /// <summary>
    /// Run tool discovery and auto-update settings with discovered paths.
    /// Call this at startup or when the user wants to re-scan.
    /// </summary>
    public async Task<List<ToolStatus>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var results = await ToolDiscovery.CheckAllToolsAsync(GetConfiguredToolPath, ct);

        // Auto-update settings with any newly discovered paths
        var changed = false;
        foreach (var status in results.Where(s => s.IsAvailable && s.DetectedPath is not null))
        {
            var currentPath = GetConfiguredToolPath(status.Tool.Id);
            // Only update if the current setting is just the bare binary name (default)
            // and we found it at an actual path
            if (currentPath == status.Tool.DefaultBinaryName && status.DetectedPath != currentPath)
            {
                SetConfiguredToolPath(status.Tool.Id, status.DetectedPath!);
                changed = true;
            }
        }

        if (changed)
            Settings.Save();

        return results;
    }

    /// <summary>Get the currently configured path for a tool ID.</summary>
    public string GetConfiguredToolPath(string toolId) => toolId switch
    {
        "ffmpeg" => Settings.FFmpegPath,
        "ffprobe" => Settings.FFprobePath,
        "mkvmerge" => Settings.MkvMergePath,
        "x264" => Settings.X264Path,
        "x265" => Settings.X265Path,
        "vspipe" => Settings.VapourSynthPath,
        "mp4box" => Settings.Mp4BoxPath,
        _ => string.Empty,
    };

    private void SetConfiguredToolPath(string toolId, string path)
    {
        switch (toolId)
        {
            case "ffmpeg": Settings.FFmpegPath = path; break;
            case "ffprobe": Settings.FFprobePath = path; break;
            case "mkvmerge": Settings.MkvMergePath = path; break;
            case "x264": Settings.X264Path = path; break;
            case "x265": Settings.X265Path = path; break;
            case "vspipe": Settings.VapourSynthPath = path; break;
            case "mp4box": Settings.Mp4BoxPath = path; break;
        }
    }

    public void Save()
    {
        Settings.Save();
    }

    public void Dispose()
    {
        JobQueue.Dispose();
        LoggerFactory.Dispose();
    }
}
