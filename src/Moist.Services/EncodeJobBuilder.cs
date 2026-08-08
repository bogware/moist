using Moist.Core.Encoders.Audio;
using Moist.Core.Chunking;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;

namespace Moist.Services;

/// <summary>
/// Everything needed to turn one input file into an encode. Deliberately a plain data
/// object so the GUI and the CLI describe work the same way.
///
/// A record so callers can derive a variant with <c>with</c> — target-quality search
/// does exactly that once it knows which CRF to use.
/// </summary>
public sealed record EncodeRequest
{
    public required string InputFile { get; init; }
    public required string OutputFile { get; init; }
    public required string TempDirectory { get; init; }

    // Video
    public string VideoEncoderId { get; init; } = "ffmpeg-x265";
    public VideoEncodingMode Mode { get; init; } = VideoEncodingMode.ConstantRateFactor;
    public double Quality { get; init; } = 23.0;
    public int Bitrate { get; init; } = 5000;
    public VideoPreset Preset { get; init; } = VideoPreset.Medium;
    public VideoTune Tune { get; init; } = VideoTune.None;
    public int SvtAv1Preset { get; init; } = 6;
    public string VideoCustomArgs { get; init; } = string.Empty;

    /// <summary>Output bit depth. Auto follows the source.</summary>
    public VideoBitDepth BitDepth { get; init; } = VideoBitDepth.Auto;

    /// <summary>
    /// When set, the video is upscaled with Real-ESRGAN (frame-based deep model) rather than a
    /// filter. The value is the model — "x4plus" (live action) or "animevideov3" (anime); null or
    /// empty leaves normal encoding. Frame-heavy and slow, so it's capped unless AllowLong is set.
    /// </summary>
    public string? AiUpscaleModel { get; init; }
    public int AiUpscaleHeight { get; init; } = 1080;
    public bool AiUpscaleAllowLong { get; init; }

    /// <summary>RIFE AI frame-interpolation target frame rate (0 = off). See RifeVideoProcessor.</summary>
    public double RifeTargetFps { get; init; }

    /// <summary>Chroma sampling. Auto follows the source.</summary>
    public ChromaSubsampling Chroma { get; init; } = ChromaSubsampling.Auto;

    /// <summary>SVT-AV1 tuning. Null leaves the encoder on its PSNR default.</summary>
    public SvtAv1Tune? SvtAv1Tune { get; init; }

    // Audio
    public string AudioEncoderId { get; init; } = "ffmpeg-audio-opus";
    public AudioBitrateMode AudioBitrateMode { get; init; } = AudioBitrateMode.Vbr;
    public int AudioBitrate { get; init; } = 128;
    public AudioChannelMode AudioChannelMode { get; init; } = AudioChannelMode.KeepOriginal;
    public int AudioSampleRate { get; init; }
    public AudioSampleFormat AudioSampleFormat { get; init; } = AudioSampleFormat.Auto;
    public AudioNormalizationMode AudioNormalization { get; init; } = AudioNormalizationMode.None;
    public double NormalizationTarget { get; init; } = -16.0;
    public double TruePeakLimit { get; init; } = -1.0;
    public string AudioCustomArgs { get; init; } = string.Empty;

    /// <summary>
    /// Source audio stream indices to encode, in output order. Every selected track gets
    /// its own encode job and its own track in the mux, with language and title carried
    /// across. Audio-only output uses the first entry.
    /// </summary>
    public IReadOnlyList<int> AudioTracks { get; init; } = [0];

    // Codec-specific
    public int OpusComplexity { get; init; } = 10;
    public string? AacProfile { get; init; }
    public int Mp3VbrScale { get; init; } = 2;
    public int FlacCompressionLevel { get; init; } = 5;
    public int Ac3DialogueNorm { get; init; } = -31;
    public int Eac3DialogueNorm { get; init; } = -31;
    public int VorbisQuality { get; init; } = 3;

    // Output shape
    public ContainerFormat Container { get; init; } = ContainerFormat.Mkv;
    public bool AudioOnly { get; init; }
    public bool CopySubtitles { get; init; } = true;
    public string? ExternalSubtitleFile { get; init; }

    /// <summary>
    /// Source subtitle stream indices to keep. Empty means all of them.
    /// </summary>
    public IReadOnlyList<int> SubtitleTracks { get; init; } = [];

    /// <summary>
    /// Render subtitles permanently into the picture, for players that can't display
    /// them. Takes the external file when one is given, otherwise
    /// <see cref="BurnInSubtitleTrack"/> from the source.
    /// </summary>
    public bool BurnInSubtitles { get; init; }

    /// <summary>Which source subtitle stream to burn in.</summary>
    public int BurnInSubtitleTrack { get; init; }

    /// <summary>Carry chapter markers over from the source.</summary>
    public bool CopyChapters { get; init; } = true;

    /// <summary>Carry attachments (subtitle fonts) over from the source. Matroska only.</summary>
    public bool CopyAttachments { get; init; } = true;

    /// <summary>Carry container-level tags over from the source.</summary>
    public bool CopyMetadata { get; init; } = true;

    // Filters (already rendered to an engine-specific script)
    public string? FilterScript { get; init; }
    public string? FilterEngineId { get; init; }
}

/// <summary>The jobs produced for one encode, in dependency order.</summary>
public sealed class EncodePlan
{
    /// <summary>The single video encode. Null when the video was split into chunks.</summary>
    public VideoJob? Video { get; init; }

    /// <summary>Chunk encodes, empty unless chunking is in use. These run in parallel.</summary>
    public IReadOnlyList<VideoJob> ChunkJobs { get; init; } = [];

    /// <summary>Joins the chunks back together. Null unless chunking is in use.</summary>
    public ConcatJob? Concat { get; init; }

    public bool IsChunked => Concat is not null;

    /// <summary>Empty when the source has no audio at all.</summary>
    public required IReadOnlyList<AudioJob> AudioJobs { get; init; }

    public MuxJob? Mux { get; init; }

    /// <summary>Convenience for the common single-track case; null for silent sources.</summary>
    public AudioJob? Audio => AudioJobs.Count > 0 ? AudioJobs[0] : null;

    public IEnumerable<Job> AllJobs
    {
        get
        {
            if (Video is not null) yield return Video;
            foreach (var chunk in ChunkJobs) yield return chunk;
            if (Concat is not null) yield return Concat;
            foreach (var audio in AudioJobs) yield return audio;
            if (Mux is not null) yield return Mux;
        }
    }

    /// <summary>The job whose completion means the whole encode is done.</summary>
    public Job FinalJob => (Job?)Mux
        ?? AudioJobs.FirstOrDefault()
        ?? (Job?)Video
        ?? throw new InvalidOperationException("encode plan contains no jobs");
}

/// <summary>
/// Builds the job graph for an encode. Shared by the GUI and the CLI so both
/// front-ends produce byte-identical pipelines.
/// </summary>
public static class EncodeJobBuilder
{
    /// <summary>
    /// Validate a request without building anything. Returns null when it's runnable,
    /// otherwise a human-readable reason.
    /// </summary>
    public static string? Validate(EncodeRequest req, MediaFileInfo? info = null)
    {
        var silent = info is not null && info.AudioStreams.Count == 0;

        if (req.AudioOnly)
        {
            return silent
                ? "Audio-only was requested, but this file has no audio streams."
                : null;
        }

        // A silent source produces no audio stream, so audio compatibility is moot.
        var audioCodec = silent
            ? null
            : ContainerCompatibility.StreamCodecForEncoder(req.AudioEncoderId);

        return ContainerCompatibility.Validate(
            req.Container,
            ContainerCompatibility.StreamCodecForEncoder(req.VideoEncoderId),
            audioCodec);
    }

    /// <summary>
    /// Things that won't stop the encode but that the user would want to know about
    /// beforehand — mostly quiet losses that are impossible to undo afterwards.
    /// </summary>
    public static IReadOnlyList<string> Warnings(EncodeRequest req, MediaFileInfo? info, bool chunked = false)
    {
        var warnings = new List<string>();

        if (req.BurnInSubtitles && chunked)
        {
            warnings.Add(
                "Burned-in subtitles and chunked encoding don't mix: each chunk is seeked to " +
                "independently, so subtitle timings would drift after the first chunk. " +
                "Turn one of them off.");
        }

        if (info?.Video is not { } video) return warnings;

        if (video.IsHdr)
        {
            if (req.BitDepth == VideoBitDepth.Eight)
            {
                warnings.Add(
                    "This source is HDR but the output is forced to 8-bit. HDR needs 10-bit; " +
                    "the result will band badly. Use Auto or 10-bit.");
            }

            if (req.FilterScript?.Contains("tonemap", StringComparison.OrdinalIgnoreCase) == true)
            {
                warnings.Add(
                    "A tone-mapping filter is applied, so this HDR source will be converted to SDR. " +
                    "That is not reversible — remove the filter to keep it HDR.");
            }

            if (video.HasDolbyVision)
            {
                warnings.Add(
                    "This source carries a Dolby Vision RPU. Re-encoding drops it, and the result " +
                    "will play back as plain HDR10. Remuxing instead of re-encoding preserves it.");
            }

            if (video.HasHdr10Plus)
            {
                warnings.Add(
                    "This source carries HDR10+ dynamic metadata, which re-encoding drops. " +
                    "The static HDR10 base layer survives, so the result stays HDR but loses " +
                    "the per-scene tone mapping.");
            }
        }

        if (req.Chroma is ChromaSubsampling.Yuv420 &&
            video.PixelFormat.Contains("444", StringComparison.Ordinal))
        {
            warnings.Add(
                "The source is 4:4:4 but the output is forced to 4:2:0, which throws away " +
                "three quarters of the colour resolution. That cannot be recovered.");
        }

        return warnings;
    }

    /// <param name="chunks">
    /// A prepared chunk plan, or null to encode the video in one pass. Produced by
    /// <see cref="ChunkedEncode.PrepareAsync"/>, which also decides whether chunking is
    /// worth it for this source at all.
    /// </param>
    public static EncodePlan Build(
        EncodeRequest req,
        MediaFileInfo? info,
        ChunkedEncodePreparation? chunks = null,
        bool verifyChunks = true,
        string? ffprobePath = null)
    {
        Directory.CreateDirectory(req.TempDirectory);
        var outputDir = Path.GetDirectoryName(req.OutputFile);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        // A silent source gets no audio jobs at all. Asking FFmpeg to map 0:a:0 on a
        // video-only file fails the whole encode.
        var hasAudio = info is null || info.AudioStreams.Count > 0;
        var tracks = !hasAudio
            ? []
            : req.AudioTracks.Count > 0
                ? req.AudioTracks.Where(t => info is null || t < info.AudioStreams.Count).ToList()
                : [0];

        if (hasAudio && tracks.Count == 0) tracks = [0];

        // ---- Audio-only: one job straight to the final file ----
        // A single file can't hold several encoded tracks without a mux step, so
        // audio-only takes the first selection.
        if (req.AudioOnly)
        {
            if (tracks.Count == 0)
                throw new InvalidOperationException("audio-only was requested but the source has no audio streams");

            return new EncodePlan
            {
                AudioJobs =
                [
                    new AudioJob
                    {
                        Name = $"Audio: {Path.GetFileName(req.InputFile)}",
                        InputFile = req.InputFile,
                        OutputFile = req.OutputFile,
                        EncoderId = req.AudioEncoderId,
                        Settings = CreateAudioSettings(req),
                        TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
                        AudioStreamIndex = tracks[0],
                    },
                ],
            };
        }

        // ---- Video ----
        var videoSettings = CreateVideoSettings(req);
        videoSettings.Mode = req.Mode;
        videoSettings.Quality = req.Quality;
        videoSettings.Bitrate = req.Bitrate;
        videoSettings.Preset = req.Preset;
        videoSettings.Tune = req.Tune;
        videoSettings.CustomArgs = req.VideoCustomArgs;
        videoSettings.BitDepth = req.BitDepth;
        videoSettings.Chroma = req.Chroma;

        if (videoSettings is SvtAv1Settings svt && req.SvtAv1Tune is { } svtTune)
            svt.SvtTune = svtTune;

        // Burning subtitles in means rendering them into the picture, so it has to happen
        // before the encoder sees the frames — ahead of any user filters.
        var filterScript = ComposeFilters(req);

        var baseName = Path.GetFileNameWithoutExtension(req.OutputFile);
        var tempVideoFile = Path.Combine(req.TempDirectory, $"moist_v_{Guid.NewGuid():N}_{baseName}.mkv");

        // Either one video job, or many chunk jobs plus a concat that reassembles them.
        VideoJob? videoJob = null;
        var chunkJobs = new List<VideoJob>();
        ConcatJob? concatJob = null;

        if (chunks is not null)
        {
            var sourceName = Path.GetFileName(req.InputFile);

            for (var i = 0; i < chunks.Plan.Chunks.Count; i++)
            {
                // Chunks finished by an earlier run are reused as-is; the concat still
                // reads them, they just don't need encoding again.
                if (chunks.AlreadyDone.Contains(i)) continue;

                var chunk = chunks.Plan.Chunks[i];
                var settings = CreateVideoSettings(req);
                settings.Mode = req.Mode;

                // Per-scene optimisation gives each chunk its own CRF so quality is
                // uniform across the file instead of bitrate being uniform.
                settings.Quality = chunks.PerChunkQuality.TryGetValue(i, out var sceneQuality)
                    ? sceneQuality
                    : req.Quality;
                settings.Bitrate = req.Bitrate;
                settings.Preset = req.Preset;
                settings.Tune = req.Tune;
                settings.CustomArgs = req.VideoCustomArgs;
                settings.BitDepth = req.BitDepth;

                chunkJobs.Add(new VideoJob
                {
                    Name = $"Chunk {i + 1}/{chunks.Plan.Chunks.Count}: {sourceName}",
                    InputFile = req.InputFile,
                    OutputFile = chunks.ChunkFiles[i],
                    EncoderId = req.VideoEncoderId,
                    Settings = settings,
                    FilterScript = filterScript,
                    FilterEngineId = req.FilterEngineId,
                    TotalFrames = chunk.FrameCount,
                    SourceBitDepth = info?.Video?.BitDepth ?? 8,
                    TrimStart = chunk.StartTime,
                    TrimFrames = chunk.FrameCount,
                });
            }

            concatJob = new ConcatJob
            {
                Name = $"Join {chunks.Plan.Chunks.Count} chunks: {sourceName}",
                InputFile = req.InputFile,
                OutputFile = tempVideoFile,
                Inputs = [.. chunks.ChunkFiles],
                ExpectedFrames = chunks.Plan.TotalFrames,
                Verify = verifyChunks,
                FFprobePath = ffprobePath ?? "ffprobe",
                TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
                DependsOn = [.. chunkJobs.Select(j => j.Id)],
            };

            // The chunk files are scratch, but only once they've been joined.
            concatJob.FilesToCleanup.AddRange(chunks.ChunkFiles);
        }
        else
        {
            videoJob = new VideoJob
            {
                Name = $"Video: {Path.GetFileName(req.InputFile)}",
                InputFile = req.InputFile,
                OutputFile = tempVideoFile,
                EncoderId = req.VideoEncoderId,
                Settings = videoSettings,
                FilterScript = filterScript,
                FilterEngineId = req.FilterEngineId,
                TotalFrames = info?.Video?.TotalFrames ?? 0,
                SourceBitDepth = info?.Video?.BitDepth ?? 8,
                AiUpscaleModel = req.AiUpscaleModel,
                AiUpscaleHeight = req.AiUpscaleHeight,
                AiUpscaleAllowLong = req.AiUpscaleAllowLong,
                RifeTargetFps = req.RifeTargetFps,
            };
        }

        // One encode job per selected source track, each to its own temp file.
        var audioJobs = new List<AudioJob>();
        var audioInputs = new List<AudioTrackInput>();

        foreach (var (sourceIndex, position) in tracks.Select((t, i) => (t, i)))
        {
            var tempAudioFile = Path.Combine(
                req.TempDirectory, $"moist_a{position}_{Guid.NewGuid():N}_{baseName}.mka");

            var source = info?.AudioStreams.ElementAtOrDefault(sourceIndex);
            var label = source?.Language is { } lang ? $" [{lang}]" : $" #{sourceIndex}";

            audioJobs.Add(new AudioJob
            {
                Name = $"Audio{label}: {Path.GetFileName(req.InputFile)}",
                InputFile = req.InputFile,
                OutputFile = tempAudioFile,
                EncoderId = req.AudioEncoderId,
                Settings = CreateAudioSettings(req),
                TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
                AudioStreamIndex = sourceIndex,
            });

            audioInputs.Add(new AudioTrackInput
            {
                FilePath = tempAudioFile,
                Language = source?.Language,
                TrackName = source?.Title,
                IsDefault = position == 0,
            });
        }

        // ---- Subtitles ----
        var subtitleInputs = new List<SubtitleTrackInput>();
        if (req.CopySubtitles && info?.SubtitleStreams is { Count: > 0 } allSubs)
        {
            // An empty selection means everything; otherwise keep only what was asked for.
            var subs = req.SubtitleTracks.Count == 0
                ? allSubs
                : req.SubtitleTracks
                    .Where(i => i >= 0 && i < allSubs.Count)
                    .Select(i => allSubs[i])
                    .ToList();

            foreach (var sub in subs)
            {
                subtitleInputs.Add(new SubtitleTrackInput
                {
                    FilePath = req.InputFile,
                    Language = sub.Language,
                    TrackName = sub.Title,
                    IsDefault = sub.IsDefault,
                    IsForced = sub.IsForced,
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(req.ExternalSubtitleFile) && File.Exists(req.ExternalSubtitleFile))
        {
            subtitleInputs.Add(new SubtitleTrackInput
            {
                FilePath = req.ExternalSubtitleFile,
                IsDefault = subtitleInputs.Count == 0,
            });
        }

        var muxJob = new MuxJob
        {
            Name = $"Mux: {Path.GetFileName(req.OutputFile)}",
            InputFile = req.InputFile,
            OutputFile = req.OutputFile,
            MuxerId = "ffmpeg-mux",
            Container = req.Container,
            VideoInput = tempVideoFile,
            VideoCodec = ContainerCompatibility.StreamCodecForEncoder(req.VideoEncoderId),
            AudioInputs = audioInputs,
            SubtitleInputs = subtitleInputs,
            DependsOn = [videoJob?.Id ?? concatJob!.Id, .. audioJobs.Select(j => j.Id)],
            TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
            SourceFile = req.InputFile,
            CopyChapters = req.CopyChapters,
            CopyAttachments = req.CopyAttachments,
            CopyGlobalMetadata = req.CopyMetadata,
        };

        muxJob.FilesToCleanup.Add(tempVideoFile);
        muxJob.FilesToCleanup.AddRange(audioInputs.Select(a => a.FilePath));

        return new EncodePlan
        {
            Video = videoJob,
            ChunkJobs = chunkJobs,
            Concat = concatJob,
            AudioJobs = audioJobs,
            Mux = muxJob,
        };
    }

    /// <summary>
    /// Pick the settings type matching the encoder. Hardware encoders need
    /// <see cref="HardwareVideoSettings"/> so the processor knows which vendor dialect
    /// to speak — and so <c>TwoPassProcessor</c> knows to leave them alone.
    /// </summary>
    public static VideoEncoderSettings CreateVideoSettings(EncodeRequest req)
    {
        if (HardwareCapabilities.Find(req.VideoEncoderId) is { } hardware)
        {
            return new HardwareVideoSettings
            {
                Vendor = hardware.Vendor,
                Codec = hardware.Codec,
            };
        }

        return req.VideoEncoderId switch
        {
            "ffmpeg-x264" or "cli-x264" => new X264Settings(),
            "ffmpeg-x265" or "cli-x265" => new X265Settings(),
            "ffmpeg-svtav1" => new SvtAv1Settings { SvtPreset = req.SvtAv1Preset },
            _ => new VideoEncoderSettings(),
        };
    }

    /// <summary>
    /// Combine burned-in subtitles with the user's filter chain. Burn-in renders text
    /// into the picture, so it goes first — filtering the rendered result is what you
    /// want if you're also scaling.
    /// </summary>
    internal static string? ComposeFilters(EncodeRequest req)
    {
        if (!req.BurnInSubtitles) return req.FilterScript;

        var source = !string.IsNullOrWhiteSpace(req.ExternalSubtitleFile)
            ? req.ExternalSubtitleFile!
            : req.InputFile;

        var burn = $"subtitles='{EscapeForFilter(source)}'";

        // Only index into the source's own subtitle streams; an external file has one.
        if (string.IsNullOrWhiteSpace(req.ExternalSubtitleFile) && req.BurnInSubtitleTrack > 0)
            burn += $":si={req.BurnInSubtitleTrack}";

        return string.IsNullOrWhiteSpace(req.FilterScript) ? burn : $"{burn},{req.FilterScript}";
    }

    /// <summary>
    /// Escape a path for use inside a filter argument. FFmpeg's filter parser treats ':'
    /// as an option separator, so a Windows drive letter breaks the whole graph unless
    /// it's escaped and the separators are flipped.
    /// </summary>
    internal static string EscapeForFilter(string path)
        => path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");

    public static AudioEncoderSettings CreateAudioSettings(EncodeRequest req)
    {
        AudioEncoderSettings s = req.AudioEncoderId switch
        {
            "ffmpeg-audio-opus" => new OpusSettings { Complexity = req.OpusComplexity },
            "ffmpeg-audio-aac" => new AacSettings { Profile = string.IsNullOrEmpty(req.AacProfile) ? null : req.AacProfile },
            "ffmpeg-audio-mp3" => new Mp3Settings { VbrScale = req.Mp3VbrScale },
            "ffmpeg-audio-flac" => new FlacSettings { CompressionLevel = req.FlacCompressionLevel },
            "ffmpeg-audio-ac3" => new Ac3Settings { DialogueNormalization = req.Ac3DialogueNorm },
            "ffmpeg-audio-eac3" => new Eac3Settings { DialogueNormalization = req.Eac3DialogueNorm },
            "ffmpeg-audio-vorbis" => new VorbisSettings { QualityScale = req.VorbisQuality },
            _ => new AudioEncoderSettings(),
        };

        s.BitrateMode = req.AudioBitrateMode;
        s.Bitrate = req.AudioBitrate;
        s.ChannelMode = req.AudioChannelMode;
        s.SampleRate = req.AudioSampleRate > 0 ? req.AudioSampleRate : null;
        s.CustomArgs = req.AudioCustomArgs;
        s.Normalization = req.AudioNormalization;
        s.NormalizationTarget = req.NormalizationTarget;
        s.TruePeakLimit = req.TruePeakLimit;
        s.SampleFormat = req.AudioSampleFormat;
        return s;
    }

    /// <summary>File extension that matches an audio encoder, for audio-only output.</summary>
    public static string AudioFileExtension(string? encoderId) => encoderId switch
    {
        "ffmpeg-audio-opus" => ".opus",
        "ffmpeg-audio-aac" => ".m4a",
        "ffmpeg-audio-mp3" => ".mp3",
        "ffmpeg-audio-flac" => ".flac",
        "ffmpeg-audio-ac3" => ".ac3",
        "ffmpeg-audio-eac3" => ".eac3",
        "ffmpeg-audio-vorbis" => ".ogg",
        "ffmpeg-audio-copy" => ".mka",
        _ => ".mka",
    };
}
