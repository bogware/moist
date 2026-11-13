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
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(Job(depth: VideoBitDepth.Ten, height: 1080), "f/%06d.jpg", "30000/1001", "seg_00000.mkv");
        Assert.Equal("30000/1001", args[Array.IndexOf(args, "-framerate") + 1]);
        Assert.Equal("libx265", args[Array.IndexOf(args, "-c:v") + 1]);
        Assert.Equal("19", args[Array.IndexOf(args, "-crf") + 1]);
        var vf = args[Array.IndexOf(args, "-vf") + 1];
        Assert.Contains("scale=-2:1080", vf);
        // 10-bit forced -> p10 pixel format carried into the final encode.
        Assert.Equal("yuv420p10le", args[Array.IndexOf(args, "-pix_fmt") + 1]);
        // Must write to the caller's target, not vj.OutputFile — otherwise windowed segments
        // all overwrite the final file and the concat finds nothing.
        Assert.Equal("seg_00000.mkv", args[^1]);
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
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(Job(), "f/%06d.jpg", "25/1", "seg_00001.mkv", startNumber: 901);
        Assert.Equal("901", args[Array.IndexOf(args, "-start_number") + 1]);
        Assert.Equal("seg_00001.mkv", args[^1]);
    }

    [Fact]
    public void BuildEncodeArgs_Vp9AddsBv0SoCrfIsTrueConstantQuality()
    {
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(Job(encoderId: "ffmpeg-vp9"), "f/%06d.jpg", "25/1", "out.webm");
        Assert.Equal("libvpx-vp9", args[Array.IndexOf(args, "-c:v") + 1]);
        // -b:v 0 must accompany -crf; without it libvpx-vp9 falls back to its ~256 kbps default target.
        var bv = Array.IndexOf(args, "-b:v");
        Assert.True(bv >= 0 && args[bv + 1] == "0");
    }

    [Fact]
    public void BuildEncodeArgs_NonVp9OmitsBv0()
    {
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(Job(encoderId: "ffmpeg-x265"), "f/%06d.jpg", "25/1", "out.mkv");
        Assert.DoesNotContain("-b:v", args);
    }

    [Fact]
    public void RealEsrganProfile_IsPreCleanPlusModel()
    {
        var p = ProfileManager.GetBuiltInProfiles()
            .Single(x => x.Name == "AI Upscale SD to HD (Real-ESRGAN)");

        Assert.Equal("x4plus", p.AiUpscaleModel);
        Assert.Equal(VideoBitDepth.Ten, p.BitDepth);
        // Only the pre-clean filters live on the profile; the processor does the upscale + finish.
        Assert.Equal(new[] { "deblock", "hqdn3d" }, p.Filters.Select(f => f.FilterId));
    }

    [Fact]
    public void BuildExtractArgs_EmitsProgressHeartbeat()
    {
        // The extract step runs for minutes on a long source; -progress gives the stall watchdog a
        // heartbeat and the progress bar a frame count, and -nostats keeps the human stats line out.
        var args = RealEsrganVideoProcessor.BuildExtractArgs(Job(), "frames/%06d.png");
        var progress = Array.IndexOf(args, "-progress");
        Assert.True(progress >= 0 && args[progress + 1] == "pipe:1");
        Assert.Contains("-nostats", args);
        Assert.True(progress < Array.IndexOf(args, "-i"), "-progress must be a global option, before -i");
    }

    private sealed class CaptureProgress : IProgress<JobProgress>
    {
        public readonly List<JobProgress> Reports = new();
        public void Report(JobProgress value) => Reports.Add(value);
    }

    [Fact]
    public void ReportExtractProgress_MapsFrameCountOntoTheExtractSlice()
    {
        var progress = new CaptureProgress();
        var sw = new System.Diagnostics.Stopwatch();

        RealEsrganVideoProcessor.ReportExtractProgress("frame=500", 1000, progress, sw);
        var p = Assert.Single(progress.Reports);
        Assert.Equal(3.0, p.Percentage, 3);                       // 1% + 0.5 x 4%
        Assert.Equal("Extracting frames (500/1000)", p.StatusLine);

        // Other -progress keys, a missing estimate and garbage are all ignored rather than reported.
        RealEsrganVideoProcessor.ReportExtractProgress("fps=59.9", 1000, progress, sw);
        RealEsrganVideoProcessor.ReportExtractProgress("frame=500", 0, progress, sw);
        RealEsrganVideoProcessor.ReportExtractProgress("frame=abc", 1000, progress, sw);
        Assert.Single(progress.Reports);

        // Never runs past the slice even when the estimate was low.
        RealEsrganVideoProcessor.ReportExtractProgress("frame=5000", 1000, progress, sw);
        Assert.Equal(5.0, progress.Reports[^1].Percentage, 3);
    }

    // ---- Post-filter frame rate + trims (the ivtc'd Real-ESRGAN output used to play at 59.94) ----

    [Fact]
    public void BuildExtractArgs_AppliesTrims_SoAPreviewDoesNotUpscaleTheWholeFilm()
    {
        var job = Job();
        job.TrimStart = TimeSpan.FromSeconds(12.5);
        job.TrimFrames = 72;
        var args = RealEsrganVideoProcessor.BuildExtractArgs(job, "frames/%06d.png");

        var ss = Array.IndexOf(args, "-ss");
        Assert.True(ss >= 0 && ss < Array.IndexOf(args, "-i"), "-ss must precede -i (input seek)");
        Assert.Equal("12.500000", args[ss + 1]);
        Assert.Equal("72", args[Array.IndexOf(args, "-frames:v") + 1]);
        Assert.DoesNotContain("-t", args);

        var byDuration = Job();
        byDuration.TrimDuration = TimeSpan.FromSeconds(3);
        var a2 = RealEsrganVideoProcessor.BuildExtractArgs(byDuration, "f/%06d.png");
        Assert.Equal("3.000", a2[Array.IndexOf(a2, "-t") + 1]);
        Assert.DoesNotContain("-frames:v", a2);
        Assert.DoesNotContain("-ss", RealEsrganVideoProcessor.BuildExtractArgs(Job(), "f/%06d.png"));
    }

    [Fact]
    public void WorkKey_DiffersWhenOnlyTheTrimDiffers()
    {
        // A snippet preview and the full encode of the same source must never share a scratch dir,
        // or the full encode "resumes" from the preview's 72 frames.
        var full = Job();
        var preview = Job();
        preview.TrimStart = TimeSpan.FromSeconds(5);
        preview.TrimFrames = 72;
        Assert.NotEqual(RealEsrganVideoProcessor.WorkKey(full), RealEsrganVideoProcessor.WorkKey(preview));
        Assert.Equal(RealEsrganVideoProcessor.WorkKey(full), RealEsrganVideoProcessor.WorkKey(Job()));
    }

    [Fact]
    public void RateFromShowinfo_UsesTheMedianMicrosecondSpacing()
    {
        // 59.94 source through ivtc -> 23.976: pts in microseconds (settb=AVTB) alternate 41708/41709.
        var lines = new[]
        {
            "Input #0, mpegts, from 'S04E16.ts':",
            "[Parsed_showinfo_3 @ 000001] n:   0 pts:      0 pts_time:0        duration:  41708",
            "[Parsed_showinfo_3 @ 000001] n:   1 pts:  41708 pts_time:0.041708 duration:  41709",
            "[Parsed_showinfo_3 @ 000001] n:   2 pts:  83417 pts_time:0.083417 duration:  41708",
            "[Parsed_showinfo_3 @ 000001] n:   3 pts: 125125 pts_time:0.125125 duration:  41708",
            "[Parsed_showinfo_3 @ 000001] n:   4 pts: 166833 pts_time:0.166833 duration:  41709",
            "frame=    5 fps=0.0 q=-0.0 Lsize=N/A time=00:00:00.16 bitrate=N/A speed=4.5x",
        };
        var fps = RealEsrganVideoProcessor.RateFromShowinfo(lines);
        Assert.NotNull(fps);
        Assert.Equal(24000.0 / 1001, fps!.Value, 2);
        Assert.Equal("24000/1001", RealEsrganVideoProcessor.ResolveOutputFrameRate("60000/1001", fps));
    }

    [Fact]
    public void RateFromShowinfo_NeedsThreeFrames_OtherwiseTheSourceRateStands()
    {
        var two = new[] { "[Parsed_showinfo_0 @ 1] n: 0 pts: 0 pts_time:0", "[Parsed_showinfo_0 @ 1] n: 1 pts: 40000 pts_time:0.04" };
        Assert.Null(RealEsrganVideoProcessor.RateFromShowinfo(two));
        Assert.Equal("60000/1001", RealEsrganVideoProcessor.ResolveOutputFrameRate("60000/1001", RealEsrganVideoProcessor.RateFromShowinfo(two)));
        Assert.Equal("25/1", RealEsrganVideoProcessor.ResolveOutputFrameRate("25/1", null));
    }

    /// <summary>FFmpeg 5.1 (Debian, the Docker image) leaves decimate's survivors on the 30i grid:
    /// three 33 ms gaps then a 67 ms one. The median of that is 29.97; the truth is 23.976.</summary>
    [Fact]
    public void UnevenDecimateTimestamps_AreAveraged_AndSnappedToAMultipleOfTheSource()
    {
        var lines = new List<string>();
        long pts = 33367;
        for (var i = 0; i < 48; i++)
        {
            lines.Add($"[Parsed_showinfo_5 @ 0x1] n: {i} pts: {pts} pts_time:{pts / 1e6}");
            pts += (i % 4 == 1) ? 66733 : 33367;     // one 30i slot in five was dropped
        }
        var sample = RealEsrganVideoProcessor.MeasureFromShowinfo(lines);
        Assert.NotNull(sample);
        Assert.False(sample!.Value.Uniform);
        Assert.InRange(sample.Value.Fps, 23.4, 24.6);
        Assert.Equal("24000/1001", RealEsrganVideoProcessor.ResolveOutputFrameRate("60000/1001", sample.Value.Fps, sample.Value.Uniform));

        // Evenly spaced frames still take the exact path.
        var even = Enumerable.Range(0, 12).Select(i => $"showinfo n: {i} pts: {i * 41708}").ToList();
        Assert.True(RealEsrganVideoProcessor.MeasureFromShowinfo(even)!.Value.Uniform);
    }

    [Fact]
    public void BuildRateProbeArgs_RunsTheSamePreCleanThenPrintsMicrosecondPts()
    {
        var job = Job();
        job.TrimStart = TimeSpan.FromSeconds(7);
        var args = RealEsrganVideoProcessor.BuildRateProbeArgs(job);

        Assert.Equal("7.000000", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.True(Array.IndexOf(args, "-ss") < Array.IndexOf(args, "-i"));
        Assert.Equal("spp=quality=3:qp=4,hqdn3d=2:1.5:3,settb=AVTB,showinfo", args[Array.IndexOf(args, "-vf") + 1]);
        Assert.Equal(RealEsrganVideoProcessor.RateProbeFrames, args[Array.IndexOf(args, "-frames:v") + 1]);
        Assert.Equal("null", args[Array.IndexOf(args, "-f") + 1]);

        var bare = Job(); bare.FilterScript = "";
        Assert.Equal("settb=AVTB,showinfo", RealEsrganVideoProcessor.BuildRateProbeArgs(bare)[Array.IndexOf(RealEsrganVideoProcessor.BuildRateProbeArgs(bare), "-vf") + 1]);
    }

    [Theory]
    [InlineData("60000/1001", 59.94005994, "60000/1001")]   // unchanged source
    [InlineData("60000/1001", 23.97602398, "24000/1001")]   // ivtc: x 2/5
    [InlineData("60000/1001", 29.97002997, "30000/1001")]   // interleave: x 1/2
    [InlineData("30000/1001", 24.0,        "24/1")]         // fps=24 on NTSC: 24, NOT 24000/1001 (0.1% apart)
    [InlineData("30000/1001", 23.97602398, "24000/1001")]
    [InlineData("25/1",       50.0,        "50/1")]         // doubled
    [InlineData("25/1",       20.0,        "20/1")]         // decimate x 4/5 of PAL
    [InlineData("24/1",       17.3,        "17300/1000")]   // nothing to snap to: used as measured
    [InlineData("29.97",      23.97602398, "24000/1001")]   // a decimal source rate still parses
    public void ResolveOutputFrameRate_SnapsToExactRationals(string source, double measured, string expected)
        => Assert.Equal(expected, RealEsrganVideoProcessor.ResolveOutputFrameRate(source, measured));

    [Fact]
    public void EstimateTrimmedFrames_HonoursTrims()
    {
        var job = Job();
        Assert.Equal(4440, RealEsrganVideoProcessor.EstimateTrimmedFrames(job, "60000/1001", 4440));
        job.TrimDuration = TimeSpan.FromSeconds(3);
        Assert.Equal(180, RealEsrganVideoProcessor.EstimateTrimmedFrames(job, "60000/1001", 4440));
        job.TrimFrames = 72;
        Assert.Equal(72, RealEsrganVideoProcessor.EstimateTrimmedFrames(job, "60000/1001", 4440));
    }
}
