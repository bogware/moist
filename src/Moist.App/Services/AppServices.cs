using Microsoft.Extensions.Logging;
using Moist.Core.Encoders;
using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Moist.Core.Profiles;
using Moist.Core.Settings;
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

    /// <summary>Media analyzer — reads FFprobePath from Settings on each access so config changes take effect.</summary>
    public FFprobeMediaAnalyzer MediaAnalyzer => new(Settings.FFprobePath);

    /// <summary>Preview service — reads FFmpegPath from Settings on each access so config changes take effect.</summary>
    public VideoPreviewService PreviewService => new(Settings.FFmpegPath);

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
            typeof(AudioEncoderSettings), "Legacy MP3 encoder");
        RegisterAudioEncoder("ffmpeg-audio-flac", "FLAC (Lossless)", "flac",
            typeof(FlacSettings), "Lossless audio compression");
        RegisterAudioEncoder("ffmpeg-audio-ac3", "AC3 (Dolby)", "ac3",
            typeof(AudioEncoderSettings), "Dolby Digital audio");
        RegisterAudioEncoder("ffmpeg-audio-eac3", "E-AC3 (Dolby+)", "eac3",
            typeof(AudioEncoderSettings), "Enhanced Dolby Digital Plus");
        RegisterAudioEncoder("ffmpeg-audio-vorbis", "Vorbis", "vorbis",
            typeof(AudioEncoderSettings), "Ogg Vorbis audio for WebM");

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
            var processor = Encoders.CreateProcessor(encoder.Id);

            // For video encoders, register a two-pass wrapper first (checked before raw)
            // and then the raw processor for single-pass/CRF modes
            if (encoder.Category == "Video")
                JobQueue.RegisterProcessor(new TwoPassProcessor(processor));

            JobQueue.RegisterProcessor(processor);
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
