using Moist.Core.Encoders.Audio;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

/// <summary>
/// Tests for FFmpegAudioProcessor argument building across all codec types.
/// Uses a testable subclass to expose BuildArguments without launching processes.
/// </summary>
public class AudioProcessorTests
{
    private static AudioJob CreateAudioJob(AudioEncoderSettings settings, string encoderId, string codec)
    {
        return new AudioJob
        {
            Name = "Test Audio",
            InputFile = "/tmp/input.mkv",
            OutputFile = "/tmp/output.mka",
            EncoderId = $"ffmpeg-audio-{codec}",
            Settings = settings,
            TotalDurationSeconds = 120.0,
            AudioStreamIndex = 0,
        };
    }

    private static string BuildArgs(string codec, AudioEncoderSettings settings)
    {
        var processor = new TestableAudioProcessor("ffmpeg", codec);
        var job = CreateAudioJob(settings, $"ffmpeg-audio-{codec}", codec);
        return processor.TestBuildArguments(job);
    }

    // === Opus ===

    [Fact]
    public void Opus_DefaultSettings_HasCodecAndBitrate()
    {
        var args = BuildArgs("opus", new OpusSettings());
        Assert.Contains("-c:a libopus", args);
        Assert.Contains("-b:a 128k", args);
        Assert.Contains("-compression_level 10", args);
    }

    [Fact]
    public void Opus_CustomComplexity_IsIncluded()
    {
        var args = BuildArgs("opus", new OpusSettings { Complexity = 5 });
        Assert.Contains("-compression_level 5", args);
    }

    [Fact]
    public void Opus_VbrMode_EmitsVbrOn()
    {
        var args = BuildArgs("opus", new OpusSettings { BitrateMode = AudioBitrateMode.Vbr });
        Assert.Contains("-vbr on", args);
    }

    [Fact]
    public void Opus_CbrMode_EmitsVbrOff()
    {
        var args = BuildArgs("opus", new OpusSettings { BitrateMode = AudioBitrateMode.Cbr });
        Assert.Contains("-vbr off", args);
    }

    [Fact]
    public void Opus_ConstrainedVbrMode_EmitsVbrConstrained()
    {
        var args = BuildArgs("opus", new OpusSettings { BitrateMode = AudioBitrateMode.ConstrainedVbr });
        Assert.Contains("-vbr constrained", args);
    }

    // === AAC ===

    [Fact]
    public void Aac_DefaultSettings_HasCodec()
    {
        var args = BuildArgs("aac", new AacSettings());
        Assert.Contains("-c:a aac", args);
        Assert.Contains("-b:a 192k", args);
    }

    [Fact]
    public void Aac_WithProfile_IncludesProfile()
    {
        var args = BuildArgs("aac", new AacSettings { Profile = "aac_he" });
        Assert.Contains("-profile:a aac_he", args);
    }

    // === MP3 ===

    [Fact]
    public void Mp3_VbrMode_HasQualityScale()
    {
        var args = BuildArgs("mp3", new Mp3Settings { BitrateMode = AudioBitrateMode.Vbr, VbrScale = 2 });
        Assert.Contains("-c:a libmp3lame", args);
        Assert.Contains("-q:a 2", args);
        // libmp3lame takes the -q:a path in VBR and ignores -b:a, so it must not be sent.
        Assert.DoesNotContain("-b:a", args);
    }

    [Fact]
    public void Mp3_JointStereo_SetsJointStereo1()
    {
        var args = BuildArgs("mp3", new Mp3Settings { Mp3ChannelMode = Mp3ChannelMode.JointStereo });
        Assert.Contains("-joint_stereo 1", args);
    }

    [Fact]
    public void Mp3_Stereo_SetsJointStereo0()
    {
        var args = BuildArgs("mp3", new Mp3Settings { Mp3ChannelMode = Mp3ChannelMode.Stereo });
        Assert.Contains("-joint_stereo 0", args);
    }

    [Fact]
    public void Mp3_DualMono_SetsAc1()
    {
        var args = BuildArgs("mp3", new Mp3Settings { Mp3ChannelMode = Mp3ChannelMode.DualMono });
        Assert.Contains("-ac 1", args);
    }

    // === FLAC ===

    [Fact]
    public void Flac_DefaultSettings_HasCompressionLevel()
    {
        var args = BuildArgs("flac", new FlacSettings());
        Assert.Contains("-c:a flac", args);
        Assert.Contains("-compression_level 5", args);
    }

    [Fact]
    public void Flac_CustomLevel_IsIncluded()
    {
        var args = BuildArgs("flac", new FlacSettings { CompressionLevel = 12 });
        Assert.Contains("-compression_level 12", args);
    }

    // === AC3 ===

    [Fact]
    public void Ac3_DefaultSettings_NoDiagnorm()
    {
        var args = BuildArgs("ac3", new Ac3Settings());
        Assert.Contains("-c:a ac3", args);
        Assert.DoesNotContain("-dialnorm", args); // -31 = disabled
    }

    [Fact]
    public void Ac3_DialogueNorm_IsIncluded()
    {
        var args = BuildArgs("ac3", new Ac3Settings { DialogueNormalization = -24 });
        Assert.Contains("-dialnorm -24", args);
    }

    // === E-AC3 ===

    [Fact]
    public void Eac3_DefaultSettings_HasCodec()
    {
        var args = BuildArgs("eac3", new Eac3Settings());
        Assert.Contains("-c:a eac3", args);
        Assert.Contains("-b:a 448k", args);
    }

    [Fact]
    public void Eac3_MixingLevel_IsIncluded()
    {
        var args = BuildArgs("eac3", new Eac3Settings { MixingLevel = 100 });
        Assert.Contains("-mixing_level 100", args);
    }

    // === Vorbis ===

    [Fact]
    public void Vorbis_VbrMode_HasQualityScale()
    {
        var args = BuildArgs("vorbis", new VorbisSettings { BitrateMode = AudioBitrateMode.Vbr, QualityScale = 5 });
        Assert.Contains("-c:a libvorbis", args);
        Assert.Contains("-q:a 5", args);
        // libvorbis takes the -q:a path in VBR and ignores -b:a, so it must not be sent.
        Assert.DoesNotContain("-b:a", args);
    }

    // === Copy (Passthrough) ===

    [Fact]
    public void Copy_SkipsAllEncodingOptions()
    {
        var args = BuildArgs("copy", new AudioEncoderSettings());
        Assert.Contains("-c:a copy", args);
        Assert.DoesNotContain("-b:a", args);
        Assert.DoesNotContain("-af", args);
    }

    // === Sample Rate & Channels ===

    [Fact]
    public void SampleRate_IsIncluded_WhenSet()
    {
        var args = BuildArgs("opus", new OpusSettings { SampleRate = 48000 });
        Assert.Contains("-ar 48000", args);
    }

    [Fact]
    public void SampleRate_NotIncluded_WhenNull()
    {
        var args = BuildArgs("opus", new OpusSettings { SampleRate = null });
        Assert.DoesNotContain("-ar", args);
    }

    [Fact]
    public void ChannelMode_Mono_Sets1()
    {
        var args = BuildArgs("aac", new AacSettings { ChannelMode = AudioChannelMode.Mono });
        Assert.Contains("-ac 1", args);
    }

    [Fact]
    public void ChannelMode_Surround51_Sets6()
    {
        var args = BuildArgs("aac", new AacSettings { ChannelMode = AudioChannelMode.Surround51 });
        Assert.Contains("-ac 6", args);
    }

    // === Sample Format ===

    [Fact]
    public void SampleFormat_S16_IsSet()
    {
        var args = BuildArgs("flac", new FlacSettings { SampleFormat = AudioSampleFormat.S16 });
        Assert.Contains("-sample_fmt s16", args);
    }

    [Fact]
    public void SampleFormat_Auto_NotIncluded()
    {
        var args = BuildArgs("flac", new FlacSettings { SampleFormat = AudioSampleFormat.Auto });
        Assert.DoesNotContain("-sample_fmt", args);
    }

    [Fact]
    public void SampleFormat_S24_MapsToS32_NotInvalidS24()
    {
        // FFmpeg has no s24 sample format; 24-bit audio lives in the s32 container.
        var args = BuildArgs("flac", new FlacSettings { SampleFormat = AudioSampleFormat.S24 });
        Assert.DoesNotContain("-sample_fmt s24", args);
        Assert.Contains("-sample_fmt s32", args);
    }

    [Fact]
    public void SampleFormat_S24_Flac_MarksTrue24BitDepth()
    {
        var args = BuildArgs("flac", new FlacSettings { SampleFormat = AudioSampleFormat.S24 });
        Assert.Contains("-bits_per_raw_sample 24", args);
    }

    [Fact]
    public void SampleFormat_S24_NonFlac_NoBitsPerRawSample()
    {
        var args = BuildArgs("opus", new OpusSettings { SampleFormat = AudioSampleFormat.S24 });
        Assert.Contains("-sample_fmt s32", args);
        Assert.DoesNotContain("-bits_per_raw_sample", args);
    }

    // === Normalization ===

    [Fact]
    public void Normalization_EbuR128_HasLoudnorm()
    {
        var args = BuildArgs("aac", new AacSettings
        {
            Normalization = AudioNormalizationMode.EbuR128,
            NormalizationTarget = -16.0,
            TruePeakLimit = -1.0
        });
        Assert.Contains("-af", args);
        Assert.Contains("loudnorm=I=-16", args);
        Assert.Contains("TP=-1", args);
    }

    [Fact]
    public void Normalization_Peak_HasAcompressor()
    {
        var args = BuildArgs("aac", new AacSettings
        {
            Normalization = AudioNormalizationMode.Peak,
            NormalizationTarget = -3.0,
            TruePeakLimit = -1.0
        });
        Assert.Contains("-af", args);
        Assert.Contains("acompressor", args);
    }

    [Fact]
    public void Normalization_Rms_HasDynaudnorm()
    {
        var args = BuildArgs("aac", new AacSettings
        {
            Normalization = AudioNormalizationMode.Rms,
            NormalizationTarget = -16.0
        });
        Assert.Contains("-af", args);
        Assert.Contains("dynaudnorm", args);
    }

    [Fact]
    public void Normalization_None_NoFilter()
    {
        var args = BuildArgs("aac", new AacSettings { Normalization = AudioNormalizationMode.None });
        Assert.DoesNotContain("-af", args);
    }

    // === Custom Args ===

    [Fact]
    public void CustomArgs_AreAppended()
    {
        var args = BuildArgs("opus", new OpusSettings { CustomArgs = "-map_metadata 0" });
        Assert.Contains("-map_metadata 0", args);
    }

    // === Audio Stream Index ===

    [Fact]
    public void AudioStreamIndex_IsUsed()
    {
        var processor = new TestableAudioProcessor("ffmpeg", "opus");
        var job = CreateAudioJob(new OpusSettings(), "ffmpeg-audio-opus", "opus");
        job.AudioStreamIndex = 2;
        var args = processor.TestBuildArguments(job);
        Assert.Contains("-map 0:a:2", args);
    }

    /// <summary>
    /// Testable subclass that exposes BuildArguments publicly.
    /// </summary>
    private class TestableAudioProcessor : FFmpegAudioProcessor
    {
        public TestableAudioProcessor(string ffmpegPath, string codec) : base(ffmpegPath, codec) { }
        public string TestBuildArguments(Job job) => BuildArguments(job);
    }
}
