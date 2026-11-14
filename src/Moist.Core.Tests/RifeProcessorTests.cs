using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

public class RifeProcessorTests
{
    private static VideoJob Job(double targetFps = 60, string? upscale = null) => new()
    {
        InputFile = "in.mkv",
        OutputFile = "out.mkv",
        EncoderId = "ffmpeg-x265",
        Settings = new X265Settings { Mode = VideoEncodingMode.ConstantRateFactor, Quality = 20, Preset = VideoPreset.Medium },
        RifeTargetFps = targetFps,
        AiUpscaleModel = upscale,
        SourceBitDepth = 8,
    };

    [Fact]
    public void CanProcess_WhenRifeRequestedAndNotAlsoUpscaling()
    {
        var p = new RifeVideoProcessor("ffmpeg", "ffprobe", "rife", "tmp");
        Assert.True(p.CanProcess(Job(targetFps: 60)));
        Assert.False(p.CanProcess(Job(targetFps: 0)));
        // The two frame-based paths aren't chained: an upscale job is left to Real-ESRGAN.
        Assert.False(p.CanProcess(Job(targetFps: 60, upscale: "x4plus")));
    }

    [Theory]
    [InlineData(100, 25, 60, 240)]   // 25 -> 60 fps
    [InlineData(100, 24, 48, 200)]   // exact 2x
    [InlineData(100, 30, 24, 100)]   // asking for fewer never drops below the source count
    public void TargetFrameCount_ScalesByRateRatio(long srcFrames, double srcFps, double targetFps, long expected)
        => Assert.Equal(expected, RifeVideoProcessor.TargetFrameCount(srcFrames, srcFps, targetFps));

    [Fact]
    public void BuildRifeArgs_RequestsTargetCountAndModel()
    {
        var args = RifeVideoProcessor.BuildRifeArgs("in", "out", "models/rife-v4.6", 240);
        Assert.Equal("240", args[Array.IndexOf(args, "-n") + 1]);
        Assert.Equal("models/rife-v4.6", args[Array.IndexOf(args, "-m") + 1]);
        Assert.Equal("%06d.png", args[Array.IndexOf(args, "-f") + 1]);
    }

    [Fact]
    public void BuildEncodeArgs_EncodesAtTargetFps()
    {
        var args = RifeVideoProcessor.BuildEncodeArgs(Job(targetFps: 60), "f/%06d.png");
        Assert.Equal("60", args[Array.IndexOf(args, "-framerate") + 1]);
        Assert.Equal("libx265", args[Array.IndexOf(args, "-c:v") + 1]);
        // Non-VP9 encoders take -crf directly; no -b:v.
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void BuildEncodeArgs_Vp9AddsBv0SoCrfIsTrueConstantQuality()
    {
        var job = Job(targetFps: 60);
        job.EncoderId = "ffmpeg-vp9";
        var args = RifeVideoProcessor.BuildEncodeArgs(job, "f/%06d.png");
        Assert.Equal("libvpx-vp9", args[Array.IndexOf(args, "-c:v") + 1]);
        // -b:v 0 must accompany -crf, or libvpx-vp9 caps the stream at its ~256 kbps default target.
        var bv = Array.IndexOf(args, "-b:v");
        Assert.True(bv >= 0 && args[bv + 1] == "0");
    }
}
