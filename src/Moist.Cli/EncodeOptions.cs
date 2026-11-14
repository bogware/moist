using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Chunking;
using Moist.Core.Muxers;
using Moist.Core.Profiles;
using Moist.Filters;
using Moist.Services;

namespace Moist.Cli;

/// <summary>
/// Turns parsed command-line options into an <see cref="EncodeRequest"/>.
/// Shared by <c>encode</c> and <c>batch</c> so both accept exactly the same flags.
/// </summary>
public static class EncodeOptions
{
    public static VideoEncodingMode ParseMode(string raw) => raw.ToLowerInvariant() switch
    {
        "crf" => VideoEncodingMode.ConstantRateFactor,
        "quality" or "cq" => VideoEncodingMode.ConstantQuality,
        "bitrate" or "abr" or "1pass" => VideoEncodingMode.SinglePassBitrate,
        "twopass" or "2pass" => VideoEncodingMode.TwoPassBitrate,
        _ => throw new UsageException($"--mode expects crf, quality, bitrate or twopass (got '{raw}')"),
    };

    /// <summary>
    /// Build a request for one input. A profile, when given, supplies the baseline and
    /// any explicitly-passed flag overrides it.
    /// </summary>
    public static EncodeRequest Build(
        CommandLine cl,
        AppServices services,
        string inputFile,
        string outputFile,
        string? filterScript,
        string? filterEngineId,
        int sourceAudioTrackCount = 1)
    {
        var profile = ResolveProfile(cl, services);

        var audioEncoder = cl.String("audio-encoder")
                           ?? profile?.AudioEncoderId
                           ?? "ffmpeg-audio-opus";

        var videoEncoder = cl.String("video-encoder")
                           ?? profile?.VideoEncoderId
                           ?? "ffmpeg-x265";

        var modeRaw = cl.String("mode");
        var mode = modeRaw is not null ? ParseMode(modeRaw)
                 : profile?.VideoMode ?? VideoEncodingMode.ConstantRateFactor;

        var audioOnly = cl.Flag("audio-only") || (profile?.AudioOnly ?? false);

        // Precedence: an explicit --container wins; otherwise the output file's own extension
        // decides (so `-o out.mp4` really is MP4, and a doomed codec/container pair is rejected
        // up front instead of silently muxing Matroska into a misnamed file); then the profile;
        // then Matroska. The extension beats the profile because it's the more specific,
        // per-invocation expression of intent.
        var container = cl.Enum<ContainerFormat>("container")
                        ?? ContainerFormatExtensions.FromExtension(outputFile)
                        ?? profile?.Container
                        ?? ContainerFormat.Mkv;

        return new EncodeRequest
        {
            InputFile = inputFile,
            OutputFile = outputFile,
            TempDirectory = cl.String("temp-dir") ?? services.Settings.TempDirectory,

            VideoEncoderId = videoEncoder,
            Mode = mode,
            Quality = cl.Double("crf") ?? cl.Double("quality") ?? profile?.VideoQuality ?? 23.0,
            Bitrate = cl.Int("bitrate") ?? profile?.VideoBitrate ?? 5000,
            Preset = cl.Enum<VideoPreset>("preset") ?? profile?.VideoPreset ?? VideoPreset.Medium,
            Tune = cl.Enum<VideoTune>("tune") ?? profile?.VideoTune ?? VideoTune.None,
            SvtAv1Preset = cl.Int("av1-preset") ?? profile?.SvtAv1Preset ?? 6,
            X265StreamWrap = ParseX265Wrap(cl.String("x265-wrap")),
            VideoCustomArgs = cl.String("video-args") ?? profile?.VideoCustomArgs ?? string.Empty,
            BitDepth = cl.String("bit-depth") is { } bd ? ParseBitDepth(bd) : (profile?.BitDepth ?? VideoBitDepth.Auto),

            // Real-ESRGAN AI upscale (Tier 2). An explicit --ai-upscale wins; else the profile's.
            AiUpscaleModel = (cl.String("ai-upscale") is { Length: > 0 } m ? m : null) ?? profile?.AiUpscaleModel,
            AiUpscaleHeight = cl.Int("ai-upscale-height") ?? profile?.AiUpscaleHeight ?? 1080,
            AiUpscaleAllowLong = cl.Flag("ai-upscale-allow-long"),

            // --fps-mode rife routes framerate change through RIFE AI interpolation (a processor);
            // dup/motion modes are handled as filters in BuildFilters.
            RifeTargetFps = cl.Double("fps") is { } fps && string.Equals(cl.String("fps-mode"), "rife", StringComparison.OrdinalIgnoreCase) ? fps : 0,
            Chroma = ParseChroma(cl.String("chroma")),
            SvtAv1Tune = ParseSvtTune(cl.String("av1-tune")),
            SvtAv1FilmGrain = cl.Int("av1-film-grain") ?? 0,
            SvtAv1TileRows = cl.Int("av1-tile-rows") ?? 1,
            SvtAv1TileColumns = cl.Int("av1-tile-columns") ?? 1,
            SvtAv1KeyframeInterval = cl.Int("av1-keyint") ?? 240,

            AudioEncoderId = audioEncoder,
            AudioBitrateMode = cl.Enum<AudioBitrateMode>("audio-mode") ?? profile?.AudioBitrateMode ?? AudioBitrateMode.Vbr,
            AudioBitrate = cl.Int("audio-bitrate") ?? profile?.AudioBitrate ?? 128,
            AudioChannelMode = cl.Enum<AudioChannelMode>("channels") ?? profile?.AudioChannelMode ?? AudioChannelMode.KeepOriginal,
            AudioSampleRate = cl.Int("sample-rate") ?? profile?.AudioSampleRate ?? 0,
            AudioSampleFormat = cl.Enum<AudioSampleFormat>("sample-format") ?? profile?.AudioSampleFormat ?? AudioSampleFormat.Auto,
            AudioNormalization = cl.Enum<AudioNormalizationMode>("normalize") ?? profile?.AudioNormalization ?? AudioNormalizationMode.None,
            NormalizationTarget = cl.Double("normalize-target") ?? profile?.NormalizationTarget ?? -16.0,
            TruePeakLimit = cl.Double("true-peak") ?? profile?.TruePeakLimit ?? -1.0,
            AudioCustomArgs = cl.String("audio-args") ?? profile?.AudioCustomArgs ?? string.Empty,
            AudioTracks = ParseAudioTracks(cl, sourceAudioTrackCount),

            OpusComplexity = cl.Int("opus-complexity") ?? 10,
            AacProfile = cl.String("aac-profile"),
            Mp3VbrScale = cl.Int("mp3-vbr") ?? 2,
            FlacCompressionLevel = cl.Int("flac-level") ?? 5,
            Ac3DialogueNorm = cl.Int("dialnorm") ?? -31,
            Eac3DialogueNorm = cl.Int("dialnorm") ?? -31,
            VorbisQuality = cl.Int("vorbis-quality") ?? 3,

            Container = container,
            AudioOnly = audioOnly,
            CopySubtitles = !cl.Flag("no-subtitles"),
            ExternalSubtitleFile = cl.String("subtitle"),
            SubtitleTracks = ParseIndexList(cl, "subtitle-track"),
            BurnInSubtitles = cl.Flag("burn-subtitles"),
            BurnInSubtitleTrack = cl.Int("burn-subtitle-track") ?? 0,
            CopyChapters = !cl.Flag("no-chapters"),
            CopyAttachments = !cl.Flag("no-attachments"),
            CopyMetadata = !cl.Flag("no-metadata"),
            PreserveDynamicHdr = cl.Flag("preserve-dynamic-hdr"),

            FilterScript = filterScript,
            FilterEngineId = filterEngineId,
        };
    }

    /// <summary>
    /// Resolve which source audio tracks to encode.
    /// Accepts <c>--audio-track</c> repeated, a comma list, or <c>all</c>.
    /// </summary>
    public static IReadOnlyList<int> ParseAudioTracks(CommandLine cl, int sourceTrackCount)
    {
        var raw = cl.Many("audio-track").ToList();
        raw.AddRange(cl.Many("audio-tracks"));

        if (raw.Count == 0) return [0];

        if (raw.Any(v => v.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return Enumerable.Range(0, Math.Max(1, sourceTrackCount)).ToList();

        var indices = new List<int>();
        foreach (var part in raw.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!int.TryParse(part.Trim(), out var index) || index < 0)
                throw new UsageException($"--audio-track expects a stream index or 'all' (got '{part}')");

            if (sourceTrackCount > 0 && index >= sourceTrackCount)
                throw new UsageException(
                    $"--audio-track {index}: the source only has {sourceTrackCount} audio track(s) (0-{sourceTrackCount - 1})");

            if (!indices.Contains(index)) indices.Add(index);
        }

        return indices.Count > 0 ? indices : [0];
    }

    /// <summary>Chunking options, or null when the user didn't ask for it.</summary>
    public static ChunkingOptions? Chunking(CommandLine cl)
    {
        if (!cl.Flag("chunked") && cl.String("chunk-mode") is null) return null;

        var mode = cl.String("chunk-mode")?.ToLowerInvariant() switch
        {
            null or "scene" => ChunkMode.Scene,
            "fixed" => ChunkMode.Fixed,
            var other => throw new UsageException($"--chunk-mode expects scene or fixed (got '{other}')"),
        };

        var min = TimeSpan.FromSeconds(cl.Double("min-chunk") ?? 5);
        var max = TimeSpan.FromSeconds(cl.Double("max-chunk") ?? 60);

        if (min <= TimeSpan.Zero) throw new UsageException("--min-chunk must be greater than zero");
        if (max < min) throw new UsageException("--max-chunk cannot be smaller than --min-chunk");

        return new ChunkingOptions
        {
            Mode = mode,
            MinChunkDuration = min,
            MaxChunkDuration = max,
            SceneThreshold = cl.Double("scene-threshold") ?? 10.0,
            Resume = !cl.Flag("no-resume"),
            Verify = !cl.Flag("no-verify"),
        };
    }

    /// <summary>Accepts "auto", "420", "422", "444", or the "4:2:0" spelling.</summary>
    public static ChromaSubsampling ParseChroma(string? raw) => raw?.ToLowerInvariant().Replace(":", "") switch
    {
        null or "auto" or "source" => ChromaSubsampling.Auto,
        "420" => ChromaSubsampling.Yuv420,
        "422" => ChromaSubsampling.Yuv422,
        "444" => ChromaSubsampling.Yuv444,
        _ => throw new UsageException($"--chroma expects auto, 420, 422 or 444 (got '{raw}')"),
    };

    /// <summary>SVT-AV1 tuning by name; vq is the perceptual one.</summary>
    public static SvtAv1Tune? ParseSvtTune(string? raw) => raw?.ToLowerInvariant() switch
    {
        null => null,
        "vq" or "visual" => SvtAv1Tune.Vq,
        "psnr" => SvtAv1Tune.Psnr,
        "ssim" => SvtAv1Tune.Ssim,
        "iq" => SvtAv1Tune.Iq,
        "msssim" or "ms-ssim" => SvtAv1Tune.MsSsim,
        "vmaf" => SvtAv1Tune.Vmaf,
        _ => throw new UsageException($"--av1-tune expects vq, psnr, ssim, iq, ms-ssim or vmaf (got '{raw}')"),
    };

    /// <summary>Repeatable or comma-separated stream indices.</summary>
    public static IReadOnlyList<int> ParseIndexList(CommandLine cl, string option)
    {
        var raw = cl.Many(option);
        if (raw.Count == 0) return [];

        var indices = new List<int>();
        foreach (var part in raw.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!int.TryParse(part.Trim(), out var index) || index < 0)
                throw new UsageException($"--{option} expects a stream index (got '{part}')");
            if (!indices.Contains(index)) indices.Add(index);
        }

        return indices;
    }

    /// <summary>Accepts "auto", "8", "10", "12" — the numbers people actually think in.</summary>
    public static VideoBitDepth ParseBitDepth(string? raw) => raw?.ToLowerInvariant() switch
    {
        null or "auto" or "source" => VideoBitDepth.Auto,
        "8" or "eight" => VideoBitDepth.Eight,
        "10" or "ten" => VideoBitDepth.Ten,
        "12" or "twelve" => VideoBitDepth.Twelve,
        _ => throw new UsageException($"--bit-depth expects auto, 8, 10 or 12 (got '{raw}')"),
    };

    public static EncodingProfile? ResolveProfile(CommandLine cl, AppServices services)
    {
        var name = cl.String("profile");
        if (name is null) return null;

        var all = services.ProfileManager.GetAllProfiles();

        // An exact (case-insensitive) name always wins, even if it's a substring of another profile.
        var exact = all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // Otherwise fall back to a substring match — but if it hits more than one, don't silently
        // pick one (e.g. "Restore" once matched both the classical and AI restore profiles).
        var partial = all.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count > 1)
            throw new UsageException(
                $"'{name}' matches {partial.Count} profiles: {string.Join(", ", partial.Select(p => $"\"{p.Name}\""))}. Be more specific.");

        var names = string.Join("\n  ", all.Select(p => p.Name));
        throw new UsageException($"no profile named '{name}'. Available:\n  {names}");
    }

    /// <summary>
    /// Parse repeated <c>--filter</c> options into an FFmpeg filter script.
    /// Format: <c>id:key=value,key=value</c>, e.g. <c>resize:width=1280,height=720</c>.
    /// </summary>
    public static (string? Script, string? EngineId) BuildFilters(CommandLine cl, AppServices services, string inputFile)
    {
        var specs = cl.Many("filter");

        // Which engine turns the chain into a script: FFmpeg (a -vf string) or VapourSynth (a .vpy
        // piped through vspipe -> ffmpeg, which is what makes vs-mlrt ML super-resolution reachable).
        var engineId = (cl.String("filter-engine") ?? "ffmpeg").ToLowerInvariant();
        var engine = services.Filters.Engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new UsageException(
                         $"unknown filter engine '{engineId}'. Available: {string.Join(", ", services.Filters.Engines.Select(e => e.Id))}");

        var available = engine.GetAvailableFilters();
        var chain = new FilterChain();

        // No explicit --filter: fall back to the selected profile's own filter chain, so a profile
        // like "Restore SD to HD" applies its whole pipeline with nothing more than --profile.
        if (specs.Count == 0)
        {
            var prof = ResolveProfile(cl, services);
            if (prof is not null)
                foreach (var pf in prof.Filters)
                {
                    var pdef = available.FirstOrDefault(f => string.Equals(f.Id, pf.FilterId, StringComparison.OrdinalIgnoreCase));
                    if (pdef is not null) chain.Add(pdef, new Dictionary<string, object>(pf.Parameters));
                }
        }
        else
        {
            foreach (var spec in specs)
            {
                var colon = spec.IndexOf(':');
                var id = (colon < 0 ? spec : spec[..colon]).Trim();

                var def = available.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase))
                          ?? throw new UsageException(
                              $"unknown filter '{id}'. Run 'moist list filters' to see the {available.Count} available.");

                var values = new Dictionary<string, object>();
                if (colon >= 0)
                {
                    foreach (var pair in spec[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = pair.IndexOf('=');
                        if (eq < 0)
                            throw new UsageException($"filter '{id}': expected key=value, got '{pair}'");

                        var key = pair[..eq].Trim();
                        var raw = pair[(eq + 1)..].Trim();

                        if (!def.Parameters.ContainsKey(key))
                        {
                            var keys = string.Join(", ", def.Parameters.Keys);
                            throw new UsageException($"filter '{id}' has no parameter '{key}'. Valid: {keys}");
                        }

                        values[key] = Coerce(raw);
                    }
                }

                chain.Add(def, values);

                // The GUI constrains values with its controls; text from a command line or the HTTP
                // API reaches the filter script verbatim, so it is validated here.
                if (chain.Filters[^1].Validate() is { Count: > 0 } problems)
                    throw new UsageException($"filter '{id}': {string.Join("; ", problems)}");
            }
        }

        // --fps convenience: append a frame-rate filter (FFmpeg engine only). dup/drop -> fps;
        // motion/blend -> minterpolate. 'rife' is AI interpolation, a processor set up in Build().
        if (engineId == "ffmpeg" && cl.Double("fps") is { } targetFps)
        {
            var mode = (cl.String("fps-mode") ?? "dup").ToLowerInvariant();
            if (mode is "motion" or "blend")
                chain.Add(available.First(f => f.Id == "minterpolate"),
                    new() { ["fps"] = targetFps, ["mode"] = mode == "motion" ? "mci" : "blend" });
            else if (mode != "rife")
                chain.Add(available.First(f => f.Id == "fps"), new() { ["fps"] = targetFps });
        }
        else if (engineId != "ffmpeg" && cl.Double("fps") is { }
                 && !string.Equals(cl.String("fps-mode"), "rife", StringComparison.OrdinalIgnoreCase))
        {
            // The fps/minterpolate filters only exist on the FFmpeg engine, and RIFE (a
            // processor) is the only other route. Anything else silently drops --fps, so say so.
            Console.Error.WriteLine(
                $"warning: --fps is ignored with --filter-engine {engineId} unless --fps-mode rife is used; " +
                "the requested frame rate was not applied.");
        }

        if (chain.Filters.Count == 0) return (null, null);

        // Only the FFmpeg and VapourSynth engines have a wired encode path (a -vf string and a
        // vspipe|ffmpeg pipe respectively). AviSynth generates an .avs script that nothing consumes,
        // so FFmpegVideoProcessor would hand it to '-vf' and fail deep inside FFmpeg — reject it up
        // front, matching the GUI's guard (which likewise only fires once there are filters to run).
        if (engineId is not ("ffmpeg" or "vapoursynth"))
            throw new UsageException(
                $"the '{engineId}' filter engine can't be encoded yet — avs2pipe piping isn't built. " +
                "Use --filter-engine ffmpeg or vapoursynth, or drop the --filter options.");

        return (engine.GenerateScript(chain, inputFile), engine.Id);
    }

    private static object Coerce(string raw)
    {
        if (int.TryParse(raw, out var i)) return i;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        if (bool.TryParse(raw, out var b)) return b;
        return raw;
    }

    /// <summary>Default output path for an input, honouring --container / --audio-only.</summary>
    public static string DefaultOutputPath(CommandLine cl, AppServices services, string input, string? outputDir, string suffix)
    {
        var profile = ResolveProfile(cl, services);
        var audioOnly = cl.Flag("audio-only") || (profile?.AudioOnly ?? false);

        string ext;
        if (audioOnly)
        {
            var enc = cl.String("audio-encoder") ?? profile?.AudioEncoderId ?? "ffmpeg-audio-opus";
            ext = EncodeJobBuilder.AudioFileExtension(enc);
        }
        else
        {
            var container = cl.Enum<ContainerFormat>("container") ?? profile?.Container ?? ContainerFormat.Mkv;
            ext = container.GetExtension();
        }

        var dir = outputDir
                  ?? (string.IsNullOrWhiteSpace(services.Settings.DefaultOutputDirectory)
                      ? Path.GetDirectoryName(Path.GetFullPath(input))!
                      : services.Settings.DefaultOutputDirectory);

        return Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + suffix + ext);
    }

    /// <summary>--x265-wrap no-bframes|mkvmerge|auto (standalone x265 only).</summary>
    public static X265StreamWrap ParseX265Wrap(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "no-bframes" or "nobframes" => X265StreamWrap.NoBFrames,
        "mkvmerge" => X265StreamWrap.Mkvmerge,
        "auto" => X265StreamWrap.Auto,
        _ => throw new UsageException($"--x265-wrap must be no-bframes, mkvmerge or auto (got '{value}')"),
    };
}
