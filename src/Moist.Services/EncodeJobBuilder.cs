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

    /// <summary>
    /// The same request with every path made absolute against the current directory. Jobs can't
    /// carry relative paths: some processors run their tool in another working directory (GPU
    /// super-resolution runs FFmpeg in the shader folder so the shader resolves by bare name), and
    /// a relative <c>--temp-dir tmp</c> then pointed into the shader folder and the encode failed.
    /// </summary>
    public EncodeRequest WithAbsolutePaths() => this with
    {
        InputFile = Absolute(InputFile)!,
        OutputFile = Absolute(OutputFile)!,
        TempDirectory = Absolute(TempDirectory)!,
        ExternalSubtitleFile = Absolute(ExternalSubtitleFile),
    };

    private static string? Absolute(string? path)
        => string.IsNullOrWhiteSpace(path) || IsDevice(path) ? path : Path.GetFullPath(path);

    /// <summary>Null devices are paths too, as far as GetFullPath is concerned ("NUL" -> "C:\…\NUL").</summary>
    private static bool IsDevice(string path) => path is "NUL" or "/dev/null" or "-";

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

    /// <summary>FFmpeg filters applied after the AI upscale (rendered from the profile's
    /// <c>AiFinishFilters</c>); null keeps the processor's default light finish.</summary>
    public string? AiFinishFilterScript { get; init; }
    public bool AiUpscaleAllowLong { get; init; }

    /// <summary>RIFE AI frame-interpolation target frame rate (0 = off). See RifeVideoProcessor.</summary>
    public double RifeTargetFps { get; init; }

    /// <summary>Chroma sampling. Auto follows the source.</summary>
    public ChromaSubsampling Chroma { get; init; } = ChromaSubsampling.Auto;

    /// <summary>SVT-AV1 tuning. Null leaves the encoder on its PSNR default.</summary>
    public SvtAv1Tune? SvtAv1Tune { get; init; }

    /// <summary>SVT-AV1 film-grain synthesis strength (0 = off, 1-50). Hides banding by
    /// re-adding grain the encoder removed; 0 leaves it off.</summary>
    public int SvtAv1FilmGrain { get; init; }

    /// <summary>SVT-AV1 tile rows / columns (count, 1 = no tiling). More tiles improve threading
    /// at a small quality cost.</summary>
    public int SvtAv1TileRows { get; init; } = 1;
    public int SvtAv1TileColumns { get; init; } = 1;

    /// <summary>SVT-AV1 maximum GOP length in frames (FFmpeg -g). 0 leaves it to libsvtav1.</summary>
    public int SvtAv1KeyframeInterval { get; init; } = 240;

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

    /// <summary>
    /// Experimental, opt-in: attempt to carry Dolby Vision RPU / HDR10+ dynamic metadata across
    /// the re-encode using dovi_tool / hdr10plus_tool. Unverified binary-metadata surgery — it
    /// degrades to a warning and never corrupts the output. See <see cref="DynamicHdrPreserver"/>.
    /// </summary>
    public bool PreserveDynamicHdr { get; init; }

    /// <summary>
    /// Standalone x265 only: how its untimed HEVC stream gets timestamps. No B-frames (default,
    /// works everywhere), mkvmerge (keeps B-frames), or Auto. See <see cref="X265StreamWrap"/>.
    /// </summary>
    public X265StreamWrap X265StreamWrap { get; init; } = X265StreamWrap.NoBFrames;

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

        // Real-ESRGAN (AiUpscaleModel) and the GPU super-resolution filter (libplacebo / "superres")
        // both enlarge the picture. Stacking them double-upscales, and the Real-ESRGAN frame extractor
        // has no Vulkan device for libplacebo, so it dies with a cryptic "hardware device reference is
        // required" error mid-encode. Catch the conflict up front with an actionable message.
        if (!string.IsNullOrWhiteSpace(req.AiUpscaleModel)
            && req.FilterScript?.Contains("libplacebo", StringComparison.Ordinal) == true)
        {
            return "Both AI upscale (Real-ESRGAN) and the GPU super-resolution filter are enabled — " +
                   "they each enlarge the video, so use one or the other. Remove the \"superres\" filter " +
                   "on the Filters tab, or pick a profile that doesn't set AI upscale.";
        }

        // Tune/codec compatibility. The VideoTune set is shared across encoders, but each
        // encoder accepts only a subset: x264 takes them all, x265 has no film/stillimage,
        // and libvpx-vp9 has only psnr/ssim. An unsupported -tune makes FFmpeg abort at init,
        // so reject it up front (in the one place both front-ends validate) rather than after
        // the whole video encode. Verified empirically against FFmpeg's libx265/libvpx-vp9.
        if (!TuneAllowed(req.VideoEncoderId, req.Tune))
            return $"The '{req.Tune}' tune isn't supported by the selected encoder " +
                   $"({req.VideoEncoderId}). Pick a compatible tune, or set Tune to None.";

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
    /// Whether an encoder accepts a given <see cref="VideoTune"/>. The enum is shared, but
    /// the tune vocabularies differ: x264 supports the full set; x265 rejects film/stillimage;
    /// libvpx-vp9 supports only psnr/ssim; SVT-AV1 ignores <c>-tune</c> entirely (it uses its
    /// own <c>SvtAv1Tune</c>); FFV1 has no tune. Hardware and unknown encoders don't emit the
    /// shared <c>-tune</c>, so they're left permissive.
    /// </summary>
    internal static bool TuneAllowed(string encoderId, VideoTune tune)
    {
        if (tune == VideoTune.None) return true;
        return encoderId switch
        {
            "ffmpeg-x264" or "cli-x264" => true,
            "ffmpeg-x265" or "cli-x265" => tune is not (VideoTune.Film or VideoTune.StillImage),
            "ffmpeg-vp9" => tune is VideoTune.Psnr or VideoTune.Ssim,
            "ffmpeg-svtav1" => true,   // -tune is not emitted for SVT-AV1
            "ffmpeg-ffv1" => false,    // FFV1 has no tune parameter
            _ => true,                 // hardware / unknown: no shared -tune emitted
        };
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

        // Cadence filters change the frame count unpredictably, so the chunk frame-count
        // verification — the guard against silently lost footage — would reject the joined
        // result. Excluded for the same reason burn-in is.
        if (chunked && req.FilterScript is { } cadenceScript
            && (cadenceScript.Contains("decimate", StringComparison.Ordinal)
                || cadenceScript.Contains("fieldmatch", StringComparison.Ordinal)))
        {
            warnings.Add(
                "Frame decimation / inverse telecine and chunked encoding don't mix: these filters " +
                "change the frame count, so the chunk verification would reject the joined result. " +
                "Turn one of them off.");
        }

        // Subtitles that won't survive the chosen container. Text subs transcode into MP4/MOV
        // (mov_text) and WebM (webvtt); image-based subs (PGS/DVD) can't be converted to those,
        // and AVI/MPEG-TS hold no text subtitles at all. Those streams are dropped rather than
        // failing the mux — warn up front so it isn't a silent loss.
        if (req.CopySubtitles && !req.BurnInSubtitles && info?.SubtitleStreams is { Count: > 0 } subs)
        {
            var selected = req.SubtitleTracks.Count == 0
                ? subs
                : req.SubtitleTracks.Where(i => i >= 0 && i < subs.Count).Select(i => subs[i]);
            if (selected.Any(s => !ContainerCarriesSubtitle(req.Container, s.Codec)))
                warnings.Add(
                    $"Some subtitles can't be carried in {req.Container} and will be dropped " +
                    "(image-based PGS/DVD subs, or any subs in AVI/MPEG-TS). Use MKV to keep them all.");
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

            if (req.PreserveDynamicHdr && (video.HasDolbyVision || video.HasHdr10Plus))
            {
                warnings.Add(
                    "Dynamic-HDR passthrough is enabled — this is EXPERIMENTAL and unverified. " +
                    "It uses dovi_tool / hdr10plus_tool to re-inject the RPU/metadata after encoding; " +
                    "if the tools are missing or the step fails it is skipped without breaking the " +
                    "encode. Always check the result plays correctly before trusting it.");
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
        req = req.WithAbsolutePaths();
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
        var videoSettings = BuildVideoSettings(req, req.Quality);

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

                // Per-scene optimisation gives each chunk its own CRF so quality is uniform
                // across the file instead of bitrate being uniform. Everything else (chroma,
                // SVT knobs, bit depth, …) comes from the shared helper so a chunk can't drift
                // from a non-chunked encode.
                var chunkQuality = chunks.PerChunkQuality.TryGetValue(i, out var sceneQuality)
                    ? sceneQuality
                    : req.Quality;
                var settings = BuildVideoSettings(req, chunkQuality);

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
                AiFinishFilterScript = req.AiFinishFilterScript,
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

            // A/V alignment. The video and audio are encoded separately and the video encode
            // starts at the first video frame — but in a broadcast capture or a stream-copied
            // cut the audio typically begins ~1 s earlier (the recording starts mid-GOP), and
            // some sources have it begin later. Muxing the two temps naively plays the picture
            // out of lip-sync by that whole stagger. Anchor every track to the first video
            // frame: trim a lead at encode time, delay a lag in the mux.
            var lead = AudioLead(info, source);

            audioJobs.Add(new AudioJob
            {
                Name = $"Audio{label}: {Path.GetFileName(req.InputFile)}",
                InputFile = req.InputFile,
                OutputFile = tempAudioFile,
                EncoderId = req.AudioEncoderId,
                Settings = CreateAudioSettings(req),
                TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
                AudioStreamIndex = sourceIndex,
                TrimStart = lead > TimeSpan.Zero ? lead : null,
            });

            audioInputs.Add(new AudioTrackInput
            {
                FilePath = tempAudioFile,
                Language = source?.Language,
                TrackName = source?.Title,
                IsDefault = position == 0,
                DelayMs = lead < TimeSpan.Zero ? (int)Math.Round(-lead.TotalMilliseconds) : 0,
            });
        }

        // ---- Subtitles ----
        var subtitleInputs = new List<SubtitleTrackInput>();
        if (req.CopySubtitles && info?.SubtitleStreams is { Count: > 0 } allSubs)
        {
            // An empty selection means everything; otherwise keep only what was asked for.
            // Carry each stream's subtitle-relative index so the muxer maps exactly the chosen
            // streams — collapsing them lost the selection and mapped every subtitle in the file.
            var indices = req.SubtitleTracks.Count == 0
                ? Enumerable.Range(0, allSubs.Count).ToList()
                : req.SubtitleTracks.Where(i => i >= 0 && i < allSubs.Count).Distinct().ToList();

            foreach (var i in indices)
            {
                var sub = allSubs[i];
                // Skip subtitles the target container genuinely can't carry (image-based subs
                // into MP4/WebM can't convert to mov_text/webvtt; AVI/TS hold none). Dropping
                // them here keeps the mux from failing; Warnings() tells the user.
                if (!ContainerCarriesSubtitle(req.Container, sub.Codec)) continue;
                subtitleInputs.Add(new SubtitleTrackInput
                {
                    FilePath = req.InputFile,
                    SourceStreamIndex = i,
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
            // Experimental dynamic-HDR passthrough, only when opted in AND the source actually
            // carries the metadata (so a normal encode never touches the tools).
            PreserveDynamicHdr = req.PreserveDynamicHdr,
            SourceHasDolbyVision = req.PreserveDynamicHdr && info?.Video?.HasDolbyVision == true,
            SourceHasHdr10Plus = req.PreserveDynamicHdr && info?.Video?.HasHdr10Plus == true,
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
    /// Build the video encoder settings for a request at a given quality. Shared by the
    /// single-video branch, every chunk (which only varies <paramref name="quality"/> for
    /// per-scene optimisation), and the snippet-preview path — so no branch can silently
    /// diverge in chroma, bit depth, tune or the SVT-AV1 knobs.
    /// </summary>
    private static VideoEncoderSettings BuildVideoSettings(EncodeRequest req, double quality)
    {
        var s = CreateVideoSettings(req);
        s.Mode = req.Mode;
        s.Quality = quality;
        s.Bitrate = req.Bitrate;
        s.Preset = req.Preset;
        s.Tune = req.Tune;
        s.CustomArgs = req.VideoCustomArgs;
        s.BitDepth = req.BitDepth;
        s.Chroma = req.Chroma;
        if (s is SvtAv1Settings svt) ApplySvtAv1(svt, req);
        return s;
    }

    /// <summary>
    /// Build a single, trimmed, video-only <see cref="VideoJob"/> that reproduces exactly what
    /// the real encode would output for a span of the source — same encoder, settings, filter
    /// chain, bit depth and AI/RIFE processors. Used by the snippet-preview so the preview
    /// reflects ALL changes, not just the filter chain. Not part of the queue; the caller runs
    /// it through <see cref="JobQueue.ResolveProcessor"/> to a scratch file.
    /// </summary>
    public static VideoJob BuildPreviewVideoJob(
        EncodeRequest req, MediaFileInfo? info, TimeSpan start, TimeSpan duration, string outputFile)
    {
        var settings = BuildVideoSettings(req, req.Quality);
        var frames = info?.Video is { Framerate: > 0 } v
            ? Math.Max(1, (int)Math.Round(duration.TotalSeconds * v.Framerate))
            : 0;
        return new VideoJob
        {
            Name = $"Preview: {Path.GetFileName(req.InputFile)}",
            InputFile = req.InputFile,
            OutputFile = outputFile,
            EncoderId = req.VideoEncoderId,
            Settings = settings,
            FilterScript = ComposeFilters(req),
            FilterEngineId = req.FilterEngineId,
            SourceBitDepth = info?.Video?.BitDepth ?? 8,
            TrimStart = start,
            // Frame-exact where we know the rate (chunk-style), else fall back to a duration.
            TrimFrames = frames > 0 ? frames : null,
            TrimDuration = frames > 0 ? null : duration,
            TotalFrames = frames,
            // Carry the whole-file AI/RIFE settings so the preview shows their effect too.
            AiUpscaleModel = req.AiUpscaleModel,
            AiFinishFilterScript = req.AiFinishFilterScript,
            AiUpscaleHeight = req.AiUpscaleHeight,
            AiUpscaleAllowLong = req.AiUpscaleAllowLong,
            RifeTargetFps = req.RifeTargetFps,
        };
    }

    /// <summary>
    /// Apply the SVT-AV1-specific request fields onto its settings, shared by the single-video
    /// and chunk branches so the two can't diverge (all of these shape the encoded bytes and
    /// feed the chunk-resume fingerprint).
    /// </summary>
    private static void ApplySvtAv1(SvtAv1Settings svt, EncodeRequest req)
    {
        if (req.SvtAv1Tune is { } tune) svt.SvtTune = tune;
        svt.FilmGrain = req.SvtAv1FilmGrain;
        svt.TileRows = req.SvtAv1TileRows;
        svt.TileColumns = req.SvtAv1TileColumns;
        svt.KeyframeInterval = req.SvtAv1KeyframeInterval;
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
            "ffmpeg-x265" or "cli-x265" => new X265Settings { StreamWrap = req.X265StreamWrap },
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

    /// <summary>
    /// How far an audio stream's first packet precedes the first video frame. Positive: the
    /// audio leads, and that lead is trimmed at encode time; negative: the audio starts late,
    /// and it is delayed in the mux. Everything anchors to the first video frame because that
    /// is where the video encode — and therefore the output — begins. Zero when nothing is
    /// known (no probe, or a clean source whose streams start together).
    /// </summary>
    internal static TimeSpan AudioLead(MediaFileInfo? info, AudioStreamInfo? audio)
        => info?.Video is null || audio is null ? TimeSpan.Zero : info.Video.StartTime - audio.StartTime;

    /// <summary>
    /// Whether a container can carry a subtitle of the given source codec. Matroska carries
    /// anything; MP4/MOV/WebM carry text subs (transcoded to mov_text/webvtt) but not image-based
    /// ones (PGS/DVD); AVI and MPEG-TS carry no text subtitles at all. Mirrors the muxer's own
    /// per-container subtitle handling so the two never disagree.
    /// </summary>
    internal static bool ContainerCarriesSubtitle(ContainerFormat container, string codec) => container switch
    {
        ContainerFormat.Mkv => true,
        ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.Webm => !IsImageSubtitle(codec),
        _ => false,
    };

    private static bool IsImageSubtitle(string codec) => codec is
        "hdmv_pgs_subtitle" or "pgssub" or "dvd_subtitle" or "dvdsub" or
        "dvb_subtitle" or "dvbsub" or "xsub";

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
