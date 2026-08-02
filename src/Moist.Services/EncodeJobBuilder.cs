using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;

namespace Moist.Services;

/// <summary>
/// Everything needed to turn one input file into an encode. Deliberately a plain data
/// object so the GUI and the CLI describe work the same way.
/// </summary>
public sealed class EncodeRequest
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
    public int AudioTrackIndex { get; init; }

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

    // Filters (already rendered to an engine-specific script)
    public string? FilterScript { get; init; }
    public string? FilterEngineId { get; init; }
}

/// <summary>The jobs produced for one encode, in dependency order.</summary>
public sealed class EncodePlan
{
    public VideoJob? Video { get; init; }
    public required AudioJob Audio { get; init; }
    public MuxJob? Mux { get; init; }

    public IEnumerable<Job> AllJobs
    {
        get
        {
            if (Video is not null) yield return Video;
            yield return Audio;
            if (Mux is not null) yield return Mux;
        }
    }

    /// <summary>The job whose completion means the whole encode is done.</summary>
    public Job FinalJob => (Job?)Mux ?? Audio;
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
    public static string? Validate(EncodeRequest req)
    {
        if (req.AudioOnly) return null;

        return ContainerCompatibility.Validate(
            req.Container,
            ContainerCompatibility.StreamCodecForEncoder(req.VideoEncoderId),
            ContainerCompatibility.StreamCodecForEncoder(req.AudioEncoderId));
    }

    public static EncodePlan Build(EncodeRequest req, MediaFileInfo? info)
    {
        Directory.CreateDirectory(req.TempDirectory);
        var outputDir = Path.GetDirectoryName(req.OutputFile);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var audioSettings = CreateAudioSettings(req);

        // ---- Audio-only: one job straight to the final file ----
        if (req.AudioOnly)
        {
            return new EncodePlan
            {
                Audio = new AudioJob
                {
                    Name = $"Audio: {Path.GetFileName(req.InputFile)}",
                    InputFile = req.InputFile,
                    OutputFile = req.OutputFile,
                    EncoderId = req.AudioEncoderId,
                    Settings = audioSettings,
                    TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
                    AudioStreamIndex = req.AudioTrackIndex,
                },
            };
        }

        // ---- Video ----
        VideoEncoderSettings videoSettings = req.VideoEncoderId switch
        {
            "ffmpeg-x264" or "cli-x264" => new X264Settings(),
            "ffmpeg-x265" or "cli-x265" => new X265Settings(),
            "ffmpeg-svtav1" => new SvtAv1Settings { SvtPreset = req.SvtAv1Preset },
            _ => new VideoEncoderSettings(),
        };
        videoSettings.Mode = req.Mode;
        videoSettings.Quality = req.Quality;
        videoSettings.Bitrate = req.Bitrate;
        videoSettings.Preset = req.Preset;
        videoSettings.Tune = req.Tune;
        videoSettings.CustomArgs = req.VideoCustomArgs;

        var baseName = Path.GetFileNameWithoutExtension(req.OutputFile);
        var tempVideoFile = Path.Combine(req.TempDirectory, $"moist_v_{Guid.NewGuid():N}_{baseName}.mkv");
        var tempAudioFile = Path.Combine(req.TempDirectory, $"moist_a_{Guid.NewGuid():N}_{baseName}.mka");

        var videoJob = new VideoJob
        {
            Name = $"Video: {Path.GetFileName(req.InputFile)}",
            InputFile = req.InputFile,
            OutputFile = tempVideoFile,
            EncoderId = req.VideoEncoderId,
            Settings = videoSettings,
            FilterScript = req.FilterScript,
            FilterEngineId = req.FilterEngineId,
            TotalFrames = info?.Video?.TotalFrames ?? 0,
        };

        var audioJob = new AudioJob
        {
            Name = $"Audio: {Path.GetFileName(req.InputFile)}",
            InputFile = req.InputFile,
            OutputFile = tempAudioFile,
            EncoderId = req.AudioEncoderId,
            Settings = audioSettings,
            TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
            AudioStreamIndex = req.AudioTrackIndex,
        };

        // ---- Subtitles ----
        var subtitleInputs = new List<SubtitleTrackInput>();
        if (req.CopySubtitles && info?.SubtitleStreams is { Count: > 0 } subs)
        {
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
            AudioInputs = [new AudioTrackInput { FilePath = tempAudioFile }],
            SubtitleInputs = subtitleInputs,
            DependsOn = [videoJob.Id, audioJob.Id],
            TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
        };

        muxJob.FilesToCleanup.Add(tempVideoFile);
        muxJob.FilesToCleanup.Add(tempAudioFile);

        return new EncodePlan { Video = videoJob, Audio = audioJob, Mux = muxJob };
    }

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
