using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Profiles;

namespace Moist.Core.Tests;

public class RealEsrganProcessorTests
{
    private static VideoJob Job(string encoderId = "ffmpeg-x265", string? model = "x4plus",
        VideoBitDepth depth = VideoBitDepth.Ten, int height = 1080) => new()
    {
        InputFile = "in.mkv",
        OutputFile = "out.mkv",
        EncoderId = encoderId,
        Settings = new X265Settings
        {
            Mode = VideoEncodingMode.ConstantRateFactor,
            Quality = 19,
            Preset = VideoPreset.Medium,
            BitDepth = depth,
        },
        FilterScript = "spp=quality=3:qp=4,hqdn3d=2:1.5:3",
        SourceBitDepth = 8,
        AiUpscaleModel = model,
        AiUpscaleHeight = height,
    };

    [Fact]
    public void CanProcess_OnlyWhenAiUpscaleModelIsSet()
    {
        var p = new RealEsrganVideoProcessor("ffmpeg", "ffprobe", "realesrgan", "tmp");
        Assert.True(p.CanProcess(Job(model: "x4plus")));
        Assert.False(p.CanProcess(Job(model: null)));
        Assert.False(p.CanProcess(new AudioJob()));
    }

    [Theory]
    [InlineData("anime", "realesr-animevideov3")]
    [InlineData("animevideov3", "realesr-animevideov3")]
    [InlineData("x4plus", "realesrgan-x4plus")]
    [InlineData("live", "realesrgan-x4plus")]
    [InlineData("", "realesrgan-x4plus")]
    [InlineData(null, "realesrgan-x4plus")]
    public void ResolveModel_MapsFriendlyNames(string? input, string expected)
        => Assert.Equal(expected, RealEsrganVideoProcessor.ResolveModel(input));

    [Theory]
    [InlineData("ffmpeg-x265", "libx265")]
    [InlineData("hw-nvenc-hevc", "libx265")]
    [InlineData("ffmpeg-x264", "libx264")]
    [InlineData("ffmpeg-svtav1", "libsvtav1")]
    [InlineData("ffmpeg-vp9", "libvpx-vp9")]
    public void FinalCodec_MapsToSoftwareEncoder(string encoderId, string expected)
        => Assert.Equal(expected, RealEsrganVideoProcessor.FinalCodec(encoderId));

    [Fact]
    public void BuildExtractArgs_AppliesPreCleanAndPassesEveryFrame()
    {
        var args = RealEsrganVideoProcessor.BuildExtractArgs(Job(), "frames/%06d.png");
        Assert.Contains("frames/%06d.png", args);
        // Pre-clean filters carried through as the -vf, and passthrough sync for an exact frame count.
        var vf = Array.IndexOf(args, "-vf");
        Assert.True(vf >= 0 && args[vf + 1] == "spp=quality=3:qp=4,hqdn3d=2:1.5:3");
        Assert.Equal("0", args[Array.IndexOf(args, "-vsync") + 1]);
    }

    [Fact]
    public void BuildRealesrganArgs_SelectsModelAndJpegOutput()
    {
        var args = RealEsrganVideoProcessor.BuildRealesrganArgs(Job(model: "anime"), "in", "out", "models");
        Assert.Equal("realesr-animevideov3", args[Array.IndexOf(args, "-n") + 1]);
        Assert.Equal("jpg", args[Array.IndexOf(args, "-f") + 1]);
        Assert.Equal("models", args[Array.IndexOf(args, "-m") + 1]);
    }

    [Fact]
    public void BuildEncodeArgs_TargetsHeightWithSourceRateAndEncoderSettings()
    {
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(Job(depth: VideoBitDepth.Ten, height: 1080), "f/%06d.jpg", "30000/1001");
        Assert.Equal("30000/1001", args[Array.IndexOf(args, "-framerate") + 1]);
        Assert.Equal("libx265", args[Array.IndexOf(args, "-c:v") + 1]);
        Assert.Equal("19", args[Array.IndexOf(args, "-crf") + 1]);
        var vf = args[Array.IndexOf(args, "-vf") + 1];
        Assert.Contains("scale=-2:1080", vf);
        // 10-bit forced -> p10 pixel format carried into the final encode.
        Assert.Equal("yuv420p10le", args[Array.IndexOf(args, "-pix_fmt") + 1]);
    }

    [Fact]
    public void PlanWindows_TilesFramesExactly_NoGapsOrOverlaps()
    {
        var windows = RealEsrganVideoProcessor.PlanWindows(2050, 900);
        Assert.Equal(3, windows.Count);
        Assert.Equal((0, 900), windows[0]);
        Assert.Equal((900, 900), windows[1]);
        Assert.Equal((1800, 250), windows[2]);
        // The windows must cover every frame once — no gap loses footage, no overlap repeats it.
        Assert.Equal(2050, windows.Sum(w => w.Count));
        for (var i = 1; i < windows.Count; i++)
            Assert.Equal(windows[i - 1].Start + windows[i - 1].Count, windows[i].Start);
    }

    [Fact]
    public void BuildEncodeArgs_UsesStartNumberForWindowSegments()
    {
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(Job(), "f/%06d.jpg", "25/1", startNumber: 901);
        Assert.Equal("901", args[Array.IndexOf(args, "-start_number") + 1]);
    }

    [Fact]
    public void RealEsrganProfile_IsPreCleanPlusModel()
    {
        var p = ProfileManager.GetBuiltInProfiles()
            .Single(x => x.Name.Contains("Real-ESRGAN", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("x4plus", p.AiUpscaleModel);
        Assert.Equal(VideoBitDepth.Ten, p.BitDepth);
        // Only the pre-clean filters live on the profile; the processor does the upscale + finish.
        Assert.Equal(new[] { "deblock", "hqdn3d" }, p.Filters.Select(f => f.FilterId));
    }
}
