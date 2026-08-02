using Moist.Core.Quality;
using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>Parsing and search arithmetic — no FFmpeg, no encoding.</summary>
public class QualityTests
{
    // ---------------- metric parsing ----------------

    [Fact]
    public void VmafIsReadFromFFmpegOutput()
    {
        var parsed = QualityMeasurer.ParseVmaf(
            "[Parsed_libvmaf_4 @ 000001] VMAF score: 74.159259\n", "does-not-exist.json");

        Assert.Equal(74.159259, parsed.Score, 4);
    }

    [Fact]
    public void XpsnrWeightsLumaFourToOne()
    {
        // FFmpeg reports per-plane; the headline figure weights Y heavily because
        // that is what the eye actually notices.
        var parsed = QualityMeasurer.ParseXpsnr(
            "[Parsed_xpsnr_2 @ 000001] XPSNR  y: 34.0236  u: 28.1723  v: 27.4766  (minimum: 27.4766)\n");

        Assert.Equal((4 * 34.0236 + 28.1723 + 27.4766) / 6.0, parsed.Score, 4);
        Assert.Equal(34.0236, parsed.Components["y"], 4);
        Assert.Equal(27.4766, parsed.Minimum!.Value, 4);
    }

    [Fact]
    public void SsimUsesTheCombinedScore()
    {
        var parsed = QualityMeasurer.ParseSsim(
            "[Parsed_ssim_2 @ 1] SSIM Y:0.982430 (17.552317) U:0.976572 (16.302677) V:0.977708 (16.518562) All:0.980667 (17.136969)\n");

        Assert.Equal(0.980667, parsed.Score, 6);
        Assert.Equal(0.982430, parsed.Components["y"], 6);
    }

    [Fact]
    public void PsnrCapturesAverageAndExtremes()
    {
        var parsed = QualityMeasurer.ParsePsnr(
            "[Parsed_psnr_2 @ 1] PSNR y:30.537358 u:27.266472 v:26.523334 average:28.951879 min:22.317696 max:51.458265\n");

        Assert.Equal(28.951879, parsed.Score, 6);
        Assert.Equal(22.317696, parsed.Minimum!.Value, 6);
        Assert.Equal(51.458265, parsed.Maximum!.Value, 6);
    }

    [Fact]
    public void UnparseableOutputIsAnError()
        => Assert.Throws<InvalidOperationException>(() => QualityMeasurer.ParseVmaf("no score here", "missing.json"));

    // ---------------- filter construction ----------------

    [Fact]
    public void EveryMetricStopsAtTheShorterInput()
    {
        // Without this a 5-second sample compared against a 40-second source keeps
        // scoring a frozen frame against moving video, which dragged one real
        // measurement from 98 down to 31.
        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");

        foreach (var metric in Enum.GetValues<QualityMetric>())
        {
            var args = measurer.BuildArguments("ref.mkv", "dist.mkv", metric, new MeasureOptions(), null);
            Assert.Contains("shortest=1", args);
            Assert.Contains("repeatlast=0", args);
        }
    }

    [Fact]
    public void DistortedIsScaledUpToTheReference()
    {
        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");
        var args = measurer.BuildArguments("ref.mkv", "dist.mkv", QualityMetric.Vmaf,
            new MeasureOptions(), (1920, 1080));

        Assert.Contains("scale=1920:1080", args);
    }

    [Fact]
    public void FourKUsesTheFourKModel()
    {
        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");

        var uhd = measurer.BuildArguments("r.mkv", "d.mkv", QualityMetric.Vmaf, new MeasureOptions(), (3840, 2160));
        Assert.Contains("vmaf_4k", uhd);

        var hd = measurer.BuildArguments("r.mkv", "d.mkv", QualityMetric.Vmaf, new MeasureOptions(), (1920, 1080));
        Assert.DoesNotContain("vmaf_4k", hd);
    }

    [Fact]
    public void SamplingSeeksTheReferenceToMatch()
    {
        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");
        var args = measurer.BuildArguments("ref.mkv", "dist.mkv", QualityMetric.Vmaf,
            new MeasureOptions
            {
                ReferenceStart = TimeSpan.FromSeconds(12.5),
                ReferenceDuration = TimeSpan.FromSeconds(5),
            }, null);

        Assert.Contains("-ss 12.500", args);
        Assert.Contains("-t 5.000", args);
    }

    // ---------------- metric metadata ----------------

    [Theory]
    [InlineData("vmaf", QualityMetric.Vmaf)]
    [InlineData("VMAF", QualityMetric.Vmaf)]
    [InlineData("xpsnr", QualityMetric.Xpsnr)]
    [InlineData("ssim", QualityMetric.Ssim)]
    public void MetricNamesParse(string raw, QualityMetric expected)
        => Assert.Equal(expected, QualityMetrics.Parse(raw));

    [Fact]
    public void UnknownMetricNameIsRejected()
        => Assert.Throws<ArgumentException>(() => QualityMetrics.Parse("butteraugli"));

    [Fact]
    public void DecibelMetricsCarryTheirUnit()
    {
        Assert.Contains("dB", QualityMetric.Psnr.Format(42.5));
        Assert.DoesNotContain("dB", QualityMetric.Vmaf.Format(95));
    }

    [Fact]
    public void SsimIsShownWithEnoughPrecisionToBeUseful()
    {
        // Two decimals would render every good encode as "0.98".
        Assert.Equal("0.9807", QualityMetric.Ssim.Format(0.98066));
    }

    // ---------------- search arithmetic ----------------

    [Fact]
    public void SearchInterpolatesTowardsTheTarget()
    {
        // Bracketed by CRF 18 -> 96 and CRF 26 -> 88, target 92 sits halfway,
        // so the next probe should land near CRF 22.
        var probes = new List<QualityProbe>
        {
            new() { Crf = 18, Score = 96, MeetsTarget = true },
            new() { Crf = 26, Score = 88, MeetsTarget = false },
        };

        var next = TargetQualitySearch.NextCandidate(probes, target: 92, lo: 18, hi: 26);
        Assert.InRange(next, 21, 23);
    }

    [Fact]
    public void SearchFallsBackToBisectionWhenInterpolationIsUseless()
    {
        // Identical scores carry no gradient to interpolate along.
        var probes = new List<QualityProbe>
        {
            new() { Crf = 20, Score = 90, MeetsTarget = false },
            new() { Crf = 30, Score = 90, MeetsTarget = false },
        };

        var next = TargetQualitySearch.NextCandidate(probes, target: 95, lo: 20, hi: 30);
        Assert.Equal(25, next);
    }

    [Fact]
    public void InterpolationNeverLeavesTheBracket()
    {
        var probes = new List<QualityProbe>
        {
            new() { Crf = 20, Score = 99.9, MeetsTarget = true },
            new() { Crf = 21, Score = 30.0, MeetsTarget = false },
        };

        var next = TargetQualitySearch.NextCandidate(probes, target: 95, lo: 20, hi: 21);
        Assert.InRange(next, 20, 21);
    }

    // ---------------- sample planning ----------------

    [Fact]
    public void SamplesAvoidTitlesAndCredits()
    {
        // The first and last few percent are usually black frames or logos, which
        // score nothing like the actual content.
        var spans = TargetQualitySearch.PlanSamples(TimeSpan.FromMinutes(100), new TargetQualityOptions { Samples = 3 });

        Assert.Equal(3, spans.Count);
        Assert.True(spans[0].Start > TimeSpan.FromMinutes(4), "first sample is too close to the start");
        Assert.True(spans[^1].Start + spans[^1].Duration < TimeSpan.FromMinutes(96), "last sample runs into the credits");
    }

    [Fact]
    public void SamplesAreSpreadOutAndDoNotOverlap()
    {
        var spans = TargetQualitySearch.PlanSamples(TimeSpan.FromMinutes(30), new TargetQualityOptions { Samples = 4 });

        Assert.Equal(4, spans.Count);
        for (var i = 1; i < spans.Count; i++)
            Assert.True(spans[i].Start >= spans[i - 1].Start + spans[i - 1].Duration,
                $"sample {i} overlaps its predecessor");
    }

    [Fact]
    public void ShortSourcesAreSampledWhole()
    {
        var spans = TargetQualitySearch.PlanSamples(TimeSpan.FromSeconds(4), new TargetQualityOptions { Samples = 3 });

        Assert.Single(spans);
        Assert.Equal(TimeSpan.Zero, spans[0].Start);
    }

    [Fact]
    public void SampleCountIsCappedByWhatActuallyFits()
    {
        // 20 seconds of usable range can't hold ten 5-second samples.
        var spans = TargetQualitySearch.PlanSamples(TimeSpan.FromSeconds(22), new TargetQualityOptions { Samples = 10 });

        Assert.True(spans.Count <= 4, $"planned {spans.Count} samples into 22 seconds");
        Assert.All(spans, s => Assert.True(s.Start + s.Duration <= TimeSpan.FromSeconds(22.1)));
    }

    // ---------------- CRF ranges ----------------

    [Fact]
    public void Av1SearchesAWiderRangeThanH264()
    {
        var (_, av1Max) = TargetQualitySearch.CrfRange("ffmpeg-svtav1");
        var (_, h264Max) = TargetQualitySearch.CrfRange("ffmpeg-x264");

        Assert.True(av1Max > h264Max, "AV1's CRF scale runs higher than H.264's");
    }

    [Fact]
    public void HardwareAv1AlsoGetsTheWiderRange()
    {
        var (_, max) = TargetQualitySearch.CrfRange("hw-nvenc-av1");
        Assert.True(max > 45);
    }

    // ---------------- cache keys ----------------

    [Fact]
    public void CacheKeyChangesWithSettingsThatAffectTheResult()
    {
        var request = new EncodeRequest
        {
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            TempDirectory = Path.GetTempPath(),
            VideoEncoderId = "ffmpeg-x265",
        };
        var options = new TargetQualityOptions { Target = 95 };

        var baseline = QualityCache.BuildKey(request, options);

        Assert.NotEqual(baseline, QualityCache.BuildKey(request with { VideoEncoderId = "ffmpeg-x264" }, options));
        Assert.NotEqual(baseline, QualityCache.BuildKey(request with { Preset = Core.Encoders.Video.VideoPreset.Slow }, options));
        Assert.NotEqual(baseline, QualityCache.BuildKey(request, new TargetQualityOptions { Target = 90 }));
        Assert.NotEqual(baseline, QualityCache.BuildKey(request, new TargetQualityOptions { Target = 95, Metric = QualityMetric.Ssim }));

        // Same inputs must produce the same key, or nothing is ever a cache hit.
        Assert.Equal(baseline, QualityCache.BuildKey(request, options));
    }

    [Fact]
    public void CacheKeyIgnoresThingsThatCannotChangeTheResult()
    {
        var request = new EncodeRequest
        {
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            TempDirectory = Path.GetTempPath(),
            VideoEncoderId = "ffmpeg-x265",
        };
        var options = new TargetQualityOptions();

        // The audio codec has no bearing on which video CRF hits a video quality target.
        Assert.Equal(
            QualityCache.BuildKey(request, options),
            QualityCache.BuildKey(request with { AudioEncoderId = "ffmpeg-audio-flac" }, options));
    }
}
