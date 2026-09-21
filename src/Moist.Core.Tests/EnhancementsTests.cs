using System;
using System.IO;
using System.Threading.Tasks;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Moist.Services;
using Xunit;

namespace Moist.Core.Tests;

/// <summary>
/// Covers the release-enhancement additions: tune/codec validation, SVT-AV1 knob wiring,
/// the snippet-preview job builder, and the experimental dynamic-HDR arg builders.
/// </summary>
public class EnhancementsTests
{
    private static EncodeRequest Req(string encoder = "ffmpeg-x265") => new()
    {
        InputFile = "in.mkv",
        OutputFile = "out.mkv",
        TempDirectory = Path.Combine(Path.GetTempPath(), "moist_test_" + Guid.NewGuid().ToString("N")),
        VideoEncoderId = encoder,
    };

    // ---- Tune / codec validation (M7) ----

    [Fact]
    public void Validate_RejectsX264OnlyTuneOnX265()
    {
        Assert.NotNull(EncodeJobBuilder.Validate(Req("ffmpeg-x265") with { Tune = VideoTune.Film }));
        Assert.NotNull(EncodeJobBuilder.Validate(Req("ffmpeg-x265") with { Tune = VideoTune.StillImage }));
    }

    [Fact]
    public void Validate_AllowsFilmTuneOnX264_AndAnimationOnX265()
    {
        Assert.Null(EncodeJobBuilder.Validate(Req("ffmpeg-x264") with { Tune = VideoTune.Film }));
        // x265 accepts animation (the built-in anime profile relies on it) — must not be rejected.
        Assert.Null(EncodeJobBuilder.Validate(Req("ffmpeg-x265") with { Tune = VideoTune.Animation }));
    }

    [Fact]
    public void Validate_Vp9AllowsOnlyPsnrSsim()
    {
        var webm = ContainerFormat.Webm;
        Assert.NotNull(EncodeJobBuilder.Validate(Req("ffmpeg-vp9") with { Tune = VideoTune.Film, Container = webm }));
        Assert.NotNull(EncodeJobBuilder.Validate(Req("ffmpeg-vp9") with { Tune = VideoTune.Grain, Container = webm }));
        Assert.Null(EncodeJobBuilder.Validate(Req("ffmpeg-vp9") with { Tune = VideoTune.Ssim, Container = webm }));
    }

    // ---- SVT-AV1 knob wiring (finding L17 front-end) ----

    [Fact]
    public void Build_WiresSvtAv1KnobsIntoSettings()
    {
        var req = Req("ffmpeg-svtav1") with
        {
            SvtAv1FilmGrain = 8,
            SvtAv1TileRows = 2,
            SvtAv1TileColumns = 4,
            SvtAv1KeyframeInterval = 120,
            SvtAv1Tune = SvtAv1Tune.Vq,
        };
        var plan = EncodeJobBuilder.Build(req, info: null);
        var svt = Assert.IsType<SvtAv1Settings>(plan.Video!.Settings);
        Assert.Equal(8, svt.FilmGrain);
        Assert.Equal(2, svt.TileRows);
        Assert.Equal(4, svt.TileColumns);
        Assert.Equal(120, svt.KeyframeInterval);
        Assert.Equal(SvtAv1Tune.Vq, svt.SvtTune);
    }

    // ---- Snippet preview job builder ----

    [Fact]
    public void BuildPreviewVideoJob_AppliesTrimAndCarriesAiSettings()
    {
        var req = Req() with { AiUpscaleModel = "x4plus", AiUpscaleHeight = 2160, RifeTargetFps = 60 };
        var job = EncodeJobBuilder.BuildPreviewVideoJob(
            req, info: null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), "snippet.mkv");

        Assert.Equal(TimeSpan.FromSeconds(5), job.TrimStart);
        Assert.Equal("snippet.mkv", job.OutputFile);
        // No media info -> no frame rate -> falls back to a duration trim rather than a frame count.
        Assert.Equal(TimeSpan.FromSeconds(3), job.TrimDuration);
        Assert.Null(job.TrimFrames);
        // The preview must reflect the AI/RIFE settings, not just the filter chain.
        Assert.Equal("x4plus", job.AiUpscaleModel);
        Assert.Equal(2160, job.AiUpscaleHeight);
        Assert.Equal(60, job.RifeTargetFps);
    }

    // ---- Dynamic-HDR arg builders + safety (experimental #5) ----

    [Fact]
    public void DynamicHdr_ArgBuildersProduceExpectedTokens()
    {
        Assert.Equal(["extract-rpu", "-i", "s.hevc", "-o", "rpu.bin"],
            DynamicHdrPreserver.BuildExtractRpuArgs("s.hevc", "rpu.bin"));
        Assert.Equal(["inject-rpu", "-i", "e.hevc", "--rpu-in", "rpu.bin", "-o", "o.hevc"],
            DynamicHdrPreserver.BuildInjectRpuArgs("e.hevc", "rpu.bin", "o.hevc"));
        Assert.Contains("extract", DynamicHdrPreserver.BuildHdr10PlusExtractArgs("s.hevc", "m.json"));
    }

    [Fact]
    public async Task DynamicHdr_SkipsWhenNoMetadataRequested()
    {
        var preserver = new DynamicHdrPreserver("ffmpeg", doviToolPath: null, hdr10plusToolPath: null);
        var result = await preserver.PreserveAsync("src.mkv", "enc.mkv", hasDolbyVision: false, hasHdr10Plus: false);
        Assert.Equal(DynamicHdrOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task DynamicHdr_ReportsToolMissingRatherThanThrowing()
    {
        // Wants DV preserved but dovi_tool isn't configured: must degrade, never throw, never touch input.
        var preserver = new DynamicHdrPreserver("ffmpeg", doviToolPath: null, hdr10plusToolPath: null);
        var result = await preserver.PreserveAsync("src.mkv", "enc.mkv", hasDolbyVision: true, hasHdr10Plus: false);
        Assert.True(result.Outcome is DynamicHdrOutcome.ToolMissing or DynamicHdrOutcome.Failed);
    }
}
