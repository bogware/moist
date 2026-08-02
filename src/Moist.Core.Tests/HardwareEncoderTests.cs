using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Muxers;
using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>
/// Pure tests for the hardware encoder plumbing — no GPU required.
/// </summary>
public class HardwareEncoderTests
{
    [Theory]
    [InlineData("hw-nvenc-h264", "h264")]
    [InlineData("hw-nvenc-hevc", "hevc")]
    [InlineData("hw-nvenc-av1", "av1")]
    [InlineData("hw-qsv-vp9", "vp9")]
    [InlineData("hw-vaapi-hevc", "hevc")]
    public void HardwareEncoderIds_MapToTheirStreamCodec(string encoderId, string expected)
        => Assert.Equal(expected, ContainerCompatibility.StreamCodecForEncoder(encoderId));

    [Fact]
    public void HardwareEncoders_ParticipateInContainerValidation()
    {
        // NVENC H.264 into WebM is just as impossible as x264 into WebM.
        Assert.NotNull(ContainerCompatibility.Validate(ContainerFormat.Webm, "h264", "opus"));

        // AV1 on the GPU is fine for WebM.
        var av1 = ContainerCompatibility.StreamCodecForEncoder("hw-nvenc-av1");
        Assert.Null(ContainerCompatibility.Validate(ContainerFormat.Webm, av1, "opus"));
    }

    [Fact]
    public void EveryKnownHardwareEncoder_HasAResolvableCodec()
    {
        foreach (var encoder in HardwareCapabilities.Known)
        {
            var codec = ContainerCompatibility.StreamCodecForEncoder(encoder.Id);
            Assert.True(codec is not null,
                $"{encoder.Id} does not resolve to a stream codec, so container validation would skip it");
            Assert.Equal(encoder.Codec, codec);
        }
    }

    [Fact]
    public void KnownHardwareEncoderIds_AreUnique()
    {
        var ids = HardwareCapabilities.Known.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void BuilderProducesHardwareSettings_ForHardwareEncoders()
    {
        var settings = EncodeJobBuilder.CreateVideoSettings(new EncodeRequest
        {
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            TempDirectory = Path.GetTempPath(),
            VideoEncoderId = "hw-nvenc-hevc",
        });

        var hardware = Assert.IsType<HardwareVideoSettings>(settings);
        Assert.Equal(HardwareVendor.Nvenc, hardware.Vendor);
        Assert.Equal("hevc", hardware.Codec);
    }

    [Fact]
    public void BuilderProducesSoftwareSettings_ForSoftwareEncoders()
    {
        var settings = EncodeJobBuilder.CreateVideoSettings(new EncodeRequest
        {
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            TempDirectory = Path.GetTempPath(),
            VideoEncoderId = "ffmpeg-x265",
        });

        Assert.IsType<X265Settings>(settings);
    }

    [Fact]
    public void TwoPassProcessor_LeavesHardwareJobsAlone()
    {
        // Hardware encoders have no -pass; wrapping one would inject flags it rejects.
        var job = new VideoJob
        {
            Name = "t",
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            EncoderId = "hw-nvenc-h264",
            Settings = new HardwareVideoSettings
            {
                Vendor = HardwareVendor.Nvenc,
                Codec = "h264",
                Mode = VideoEncodingMode.TwoPassBitrate,
            },
        };

        var twoPass = new TwoPassProcessor(new AlwaysProcessor());
        Assert.False(twoPass.CanProcess(job));
    }

    [Fact]
    public void TwoPassProcessor_StillWrapsSoftwareJobs()
    {
        var job = new VideoJob
        {
            Name = "t",
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            EncoderId = "ffmpeg-x264",
            Settings = new X264Settings { Mode = VideoEncodingMode.TwoPassBitrate },
        };

        var twoPass = new TwoPassProcessor(new AlwaysProcessor());
        Assert.True(twoPass.CanProcess(job));
    }

    [Theory]
    [InlineData(VideoPreset.Ultrafast, "p1")]
    [InlineData(VideoPreset.Medium, "p4")]
    [InlineData(VideoPreset.Veryslow, "p7")]
    public void NvencPresets_SpanTheFullRange(VideoPreset preset, string expected)
        => Assert.Equal(expected, FFmpegHardwareVideoProcessor.NvencPreset(preset));

    [Fact]
    public void EveryPreset_MapsToAValidVendorPreset()
    {
        foreach (var preset in Enum.GetValues<VideoPreset>())
        {
            Assert.Matches("^p[1-7]$", FFmpegHardwareVideoProcessor.NvencPreset(preset));
            Assert.Contains(FFmpegHardwareVideoProcessor.QsvPreset(preset),
                new[] { "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" });
            Assert.Contains(FFmpegHardwareVideoProcessor.AmfQuality(preset),
                new[] { "speed", "balanced", "quality" });
        }
    }

    [Theory]
    [InlineData(0, 100)]   // best QP -> best VideoToolbox quality
    [InlineData(51, 1)]    // worst QP -> worst quality
    [InlineData(26, 49)]
    public void VideoToolboxQuality_InvertsTheQpScale(int qp, int expected)
        => Assert.Equal(expected, FFmpegHardwareVideoProcessor.QpToVideoToolboxQuality(qp));

    [Fact]
    public void FailureSummary_PrefersTheCauseOverTheConsequence()
    {
        // FFmpeg's last line is usually a generic downstream complaint.
        const string stderr = """
            [AVHWDeviceContext @ 000001] Failed to initialise VAAPI connection: -1 (unknown libva error).
            [vost#0:0 @ 000002] Error while opening encoder
            [out#0/null @ 000003] Nothing was written into output file, because at least one of its streams received no packets.
            """;

        var reason = HardwareCapabilities.SummarizeFailure(stderr, 1);

        Assert.Contains("VAAPI", reason);
        Assert.DoesNotContain("Nothing was written", reason);
    }

    [Fact]
    public void FailureSummary_StripsFFmpegComponentPrefixes()
    {
        var reason = HardwareCapabilities.SummarizeFailure(
            "[h264_amf @ 0000021] DLL amfrt64.dll failed to open\n", 1);

        Assert.StartsWith("DLL amfrt64.dll", reason);
    }

    [Fact]
    public void FailureSummary_FallsBackToTheExitCode()
        => Assert.Contains("exit code 42", HardwareCapabilities.SummarizeFailure("", 42));

    [Fact]
    public void UnknownEncoderId_IsNotMistakenForHardware()
        => Assert.Null(HardwareCapabilities.Find("ffmpeg-x265"));

    private sealed class AlwaysProcessor : IJobProcessor
    {
        public string Id => "always";
        public string DisplayName => "Always";
        public bool CanProcess(Job job) => true;
        public Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct) => Task.CompletedTask;
    }
}
