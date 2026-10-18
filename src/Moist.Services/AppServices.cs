using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Moist.Core.Chunking;
using Moist.Core.Encoders;
using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Moist.Core.Plugins;
using Moist.Core.Profiles;
using Moist.Core.Settings;
using Moist.Core.Tools;
using Moist.Filters;
using Moist.Filters.Engines;

namespace Moist.Services;

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
    public PluginLoader PluginLoader { get; }

    /// <summary>
    /// Which GPU encoders actually work here. Populated by <see cref="DetectHardwareAsync"/>;
    /// until that has run, everything reports unusable and the UI hides them.
    /// </summary>
    public HardwareCapabilities Hardware { get; }

    /// <summary>Queued encodes that should survive a crash or a closed window.</summary>
    public EncodeQueueStore QueueStore { get; }
    public string LogFilePath { get; private set; } = string.Empty;

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

    /// <summary>
    /// Renders short preview snippets through the real encode pipeline (see
    /// <see cref="SnippetPreviewService"/>). Holds no tool-path state of its own — it goes back
    /// through <see cref="JobQueue"/> and the encoder factories, which are already late-bound.
    /// </summary>
    public SnippetPreviewService SnippetPreview => _snippetPreview ??= new SnippetPreviewService(this);
    private SnippetPreviewService? _snippetPreview;

    /// <param name="minimumLogLevel">Console verbosity. The CLI raises this so logs don't mix into its output.</param>
    /// <param name="logToStandardError">
    /// Send log lines to stderr instead of stdout. Required for the CLI, whose stdout
    /// carries machine-readable results that must not be polluted.
    /// </param>
    public AppServices(LogLevel minimumLogLevel = LogLevel.Debug, bool logToStandardError = false)
    {
        // Set up logging to both console and file
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "moist", "logs");
        Directory.CreateDirectory(logDir);
        LogFilePath = Path.Combine(logDir, $"moist_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        // The console's verbosity and the session file's detail are deliberately separate.
        // minimumLogLevel is a CONSOLE concern (the CLI raises it so logs don't pollute its
        // output); the file always gets full detail, because it is what you read after
        // something went wrong. The global minimum must therefore be the lower of the two —
        // a global threshold filters before any provider sees the line.
        const LogLevel fileLevel = LogLevel.Debug;
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(fileLevel < minimumLogLevel ? fileLevel : minimumLogLevel)
                .AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(level => level >= minimumLogLevel)
                .AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                    options.SingleLine = true;
                })
                // Everything the app logs also goes to the session file. Previously only job
                // output was written there, so a failure outside a queued job — a preview, a
                // probe, a tool download — left no trace on disk once the console scrolled
                // (and the GUI has no console at all).
                .AddProvider(new Core.Diagnostics.FileLoggerProvider(LogFilePath, fileLevel));

            if (logToStandardError)
            {
                // The threshold lives on the provider options, not the formatter's.
                builder.Services.Configure<ConsoleLoggerOptions>(
                    o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            }
        });

        // Start a background task to write job logs to file
        _ = CleanOldLogFiles(logDir);

        var logger = LoggerFactory.CreateLogger<AppServices>();
        logger.LogInformation("moist starting up — initializing services");

        Settings = MoistSettings.Load();
        Encoders = new EncoderRegistry();
        Filters = new FilterRegistry();
        JobQueue = new JobQueue(Settings.MaxConcurrentJobs, LoggerFactory);
        // The GUI is long-lived — a session can run hundreds of encodes, each retaining a job
        // object and its rolling output-log buffer. Bound it so memory stays flat. (`moist serve`
        // sets its own tighter cap; short-lived CLI commands don't need one.)
        JobQueue.MaxCompletedJobsRetained = 500;
        ProfileManager = new ProfileManager();
        PluginSources = new PluginSourceManager();
        FilterPresets = new FilterPresetManager();
        ToolDiscovery = new ToolDiscovery(LoggerFactory);
        PluginLoader = new PluginLoader(LoggerFactory);
        Hardware = new HardwareCapabilities(LoggerFactory);
        QueueStore = new EncodeQueueStore(LoggerFactory);

        // Clear a stored encode as soon as its final job settles, so a finished or failed
        // encode doesn't reappear on the next launch.
        JobQueue.JobStatusChanged += (_, job) =>
        {
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                QueueStore.NotifyJobFinished(job.Id);
        };

        RegisterDefaultEncoders();
        RegisterDefaultFilters();
        RegisterDefaultProcessors();

        // Load plugins (adds custom encoders/processors from plugins directory)
        try
        {
            PluginLoader.LoadPlugins(Encoders, JobQueue);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Plugin loading failed — continuing without plugins");
        }
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

        // Hardware encoders. Registered unconditionally so the registry is stable, but
        // hidden from users until HardwareCapabilities confirms they actually work —
        // an encoder being compiled into FFmpeg says nothing about the installed GPU.
        foreach (var hw in HardwareCapabilities.Known)
        {
            var definition = hw; // capture for the closure
            Encoders.Register(new EncoderInfo
            {
                Id = definition.Id,
                Name = definition.Label,
                Category = "Video",
                SettingsType = typeof(HardwareVideoSettings),
                Description = definition.Experimental
                    ? definition.Description + " Not yet verified on real hardware."
                    : definition.Description,
            }, () => new FFmpegHardwareVideoProcessor(
                Settings.FFmpegPath, definition.Id, definition.FfmpegName, definition.Vendor));
        }

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
        }, () => new X265CliProcessor(Settings.X265Path, Settings.FFmpegPath, Settings.FFprobePath));

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

        // Joins chunk encodes back together
        Encoders.Register(new EncoderInfo
        {
            Id = "ffmpeg-concat",
            Name = "FFmpeg Concat",
            Category = "Muxer",
            SettingsType = typeof(object),
            Description = "Joins encoded chunks without re-encoding",
        }, () => new FFmpegConcatProcessor(Settings.FFmpegPath));

        // Muxer
        Encoders.Register(new EncoderInfo
        {
            Id = "ffmpeg-mux",
            Name = "FFmpeg Muxer",
            Category = "Muxer",
            SettingsType = typeof(object),
            Description = "Universal muxer via FFmpeg",
            OutputFormats = ["mkv", "mp4", "webm", "avi", "ts", "mov"],
        }, () => new FFmpegMuxProcessor(
            Settings.FFmpegPath,
            // Late-bound like the other tool paths (read per job). Empty -> null so the
            // experimental HDR passthrough reports "tool missing" rather than shelling nothing.
            string.IsNullOrWhiteSpace(Settings.DoviToolPath) ? null : Settings.DoviToolPath,
            string.IsNullOrWhiteSpace(Settings.Hdr10PlusToolPath) ? null : Settings.Hdr10PlusToolPath,
            LoggerFactory));
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
        // Wire the GPU superres availability probe. The closure reads the FFmpeg path lazily, so
        // changing it in Settings takes effect on the next check; SuperResDetector caches the result.
        var ffmpeg = new FFmpegFilterEngine
        {
            IsSuperResAvailable = () => Moist.Core.Encoders.Video.SuperResDetector.IsAvailable(Settings.FFmpegPath),
        };
        Filters.Register(ffmpeg);
        Filters.Register(new VapourSynthFilterEngine(Settings.VapourSynthPath));
        Filters.Register(new AviSynthFilterEngine());
    }

    private void RegisterDefaultProcessors()
    {
        // Real-ESRGAN upscaler runs before the normal encoders: an AI-upscale VideoJob still names a
        // real encoder (x265, …), so FFmpegVideoProcessor.CanProcess would also claim it — this must
        // be registered first so it wins via CanProcess (AiUpscaleModel set). Factory: per-job state.
        JobQueue.RegisterProcessorFactory(() => new RealEsrganVideoProcessor(
            Settings.FFmpegPath, Settings.FFprobePath, Settings.RealEsrganPath, Settings.TempDirectory, LoggerFactory));

        // RIFE AI frame interpolation, likewise before the normal encoders (CanProcess: RifeTargetFps set).
        JobQueue.RegisterProcessorFactory(() => new RifeVideoProcessor(
            Settings.FFmpegPath, Settings.FFprobePath, Settings.RifePath, Settings.TempDirectory, LoggerFactory));

        // VapourSynth jobs (FilterEngineId == "vapoursynth") are piped vspipe -> ffmpeg. This is the
        // seam that lets a .vpy (incl. vs-mlrt ML nodes) reach the encoder; the FFmpeg engine's -vf
        // path can't run it. Registered before the normal encoders, which key on EncoderId.
        JobQueue.RegisterProcessorFactory(() => new VapourSynthPipeProcessor(
            Settings.FFmpegPath, Settings.VapourSynthPath));

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
    /// Probe which GPU encoders work on this machine. Safe to call repeatedly; results
    /// are cached until <paramref name="force"/> is set.
    /// </summary>
    public Task<IReadOnlyList<HardwareEncoderStatus>> DetectHardwareAsync(
        bool force = false, CancellationToken ct = default)
        => Hardware.DetectAsync(Settings.FFmpegPath, force, ct);

    /// <summary>Video encoders worth showing: software plus any hardware that actually works.</summary>
    public IEnumerable<EncoderInfo> GetUsableVideoEncoders()
        => Encoders.GetVideoEncoders()
            .Where(e => !e.Id.StartsWith("hw-", StringComparison.Ordinal) || Hardware.IsUsable(e.Id));

    private Task<List<ToolStatus>>? _toolDiscovery;
    private readonly object _toolDiscoveryLock = new();

    /// <summary>
    /// Run tool discovery and auto-update settings with discovered paths.
    ///
    /// The result is shared: the main window and the settings tab both want it at
    /// startup, and each scan spawns a process per known tool. Pass
    /// <paramref name="force"/> when the user explicitly asks to re-scan.
    /// </summary>
    public Task<List<ToolStatus>> DiscoverToolsAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_toolDiscoveryLock)
        {
            // A faulted scan shouldn't be cached forever.
            if (force || _toolDiscovery is null || _toolDiscovery.IsFaulted)
                _toolDiscovery = RunToolDiscoveryAsync(ct);

            return _toolDiscovery;
        }
    }

    /// <inheritdoc cref="DiscoverToolsAsync(bool, CancellationToken)"/>
    public Task<List<ToolStatus>> DiscoverToolsAsync(CancellationToken ct)
        => DiscoverToolsAsync(force: false, ct);

    private async Task<List<ToolStatus>> RunToolDiscoveryAsync(CancellationToken ct)
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
        "dovi_tool" => Settings.DoviToolPath,
        "hdr10plus_tool" => Settings.Hdr10PlusToolPath,
        "x264" => Settings.X264Path,
        "x265" => Settings.X265Path,
        "vspipe" => Settings.VapourSynthPath,
        "mp4box" => Settings.Mp4BoxPath,
        "realesrgan" => Settings.RealEsrganPath,
        "rife" => Settings.RifePath,
        _ => string.Empty,
    };

    /// <summary>
    /// Download and install a known tool to the moist tools directory, record its path in Settings,
    /// and persist. Returns the installed binary path, or null if there's no download for this platform.
    /// This is what makes tools installable from the CLI (<c>moist tools install</c>), not just the GUI.
    /// </summary>
    public async Task<string?> InstallToolAsync(string toolId,
        IProgress<(string Status, double Percent)>? progress = null, CancellationToken ct = default)
    {
        var tool = ToolDiscovery.KnownTools.FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException(
                       $"unknown tool '{toolId}'. Known: {string.Join(", ", Core.Tools.ToolDiscovery.KnownTools.Select(t => t.Id))}");

        var path = await ToolDiscovery.DownloadAndInstallAsync(tool, progress, ct);
        if (path is not null)
        {
            SetConfiguredToolPath(tool.Id, path);
            Save();
        }
        return path;
    }

    private void SetConfiguredToolPath(string toolId, string path)
    {
        switch (toolId)
        {
            case "ffmpeg": Settings.FFmpegPath = path; break;
            case "ffprobe": Settings.FFprobePath = path; break;
            case "mkvmerge": Settings.MkvMergePath = path; break;
            case "dovi_tool": Settings.DoviToolPath = path; break;
            case "hdr10plus_tool": Settings.Hdr10PlusToolPath = path; break;
            case "x264": Settings.X264Path = path; break;
            case "x265": Settings.X265Path = path; break;
            case "vspipe": Settings.VapourSynthPath = path; break;
            case "mp4box": Settings.Mp4BoxPath = path; break;
            case "realesrgan": Settings.RealEsrganPath = path; break;
            case "rife": Settings.RifePath = path; break;
        }
    }

    public void Save()
    {
        Settings.Save();
    }

    /// <summary>
    /// Write all log output from a completed job to the session log file.
    /// Called automatically when a job finishes.
    /// </summary>
    public void WriteJobLogToFile(Core.Jobs.Job job)
    {
        if (string.IsNullOrEmpty(LogFilePath)) return;
        try
        {
            var lines = new List<string>
            {
                $"",
                $"=== {job.Name} ({job.JobType}) — {job.Status} ===",
                $"    ID: {job.Id}",
                $"    Input: {job.InputFile}",
                $"    Output: {job.OutputFile}",
                $"    Started: {job.StartedAt:O}",
                $"    Completed: {job.CompletedAt:O}",
            };
            if (job.ErrorMessage is not null)
                lines.Add($"    Error: {job.ErrorMessage}");
            lines.Add("--- Output Log ---");
            lines.AddRange(job.OutputLog);
            lines.Add("--- End ---");
            lines.Add("");

            File.AppendAllLines(LogFilePath, lines);
        }
        catch { /* best effort */ }
    }

    /// <summary>Keep only the last 10 log files.</summary>
    private static Task CleanOldLogFiles(string logDir)
    {
        return Task.Run(() =>
        {
            try
            {
                var logFiles = Directory.GetFiles(logDir, "moist_*.log")
                    .OrderByDescending(f => f)
                    .Skip(10)
                    .ToList();
                foreach (var old in logFiles)
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
        });
    }

    public void Dispose()
    {
        JobQueue.Dispose();
        LoggerFactory.Dispose();
    }
}
