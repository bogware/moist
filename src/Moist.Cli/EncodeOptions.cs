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

        var container = cl.Enum<ContainerFormat>("container")
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
            VideoCustomArgs = cl.String("video-args") ?? profile?.VideoCustomArgs ?? string.Empty,
            BitDepth = ParseBitDepth(cl.String("bit-depth")),

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
            CopyChapters = !cl.Flag("no-chapters"),
            CopyAttachments = !cl.Flag("no-attachments"),
            CopyMetadata = !cl.Flag("no-metadata"),

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
        var match = all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            var names = string.Join("\n  ", all.Select(p => p.Name));
            throw new UsageException($"no profile named '{name}'. Available:\n  {names}");
        }

        return match;
    }

    /// <summary>
    /// Parse repeated <c>--filter</c> options into an FFmpeg filter script.
    /// Format: <c>id:key=value,key=value</c>, e.g. <c>resize:width=1280,height=720</c>.
    /// </summary>
    public static (string? Script, string? EngineId) BuildFilters(CommandLine cl, AppServices services, string inputFile)
    {
        var specs = cl.Many("filter");
        if (specs.Count == 0) return (null, null);

        var engine = services.Filters.Engines.FirstOrDefault(e => e.Id == "ffmpeg")
                     ?? throw new UsageException("the FFmpeg filter engine is not registered");

        var available = engine.GetAvailableFilters();
        var chain = new FilterChain();

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
        }

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
}
