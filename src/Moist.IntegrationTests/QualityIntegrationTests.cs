using Moist.Core.Encoders.Video;
using Moist.Core.Quality;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Real measurements and real searches.
///
/// These use <see cref="MediaFixture.NaturalVideo"/> rather than the synthetic test
/// pattern: FFmpeg's testsrc2 is high-frequency noise that VMAF scores into a narrow
/// band regardless of CRF, which makes it useless for asserting that quality responds
/// to settings.
/// </summary>
[Collection("media")]
public sealed class QualityIntegrationTests(MediaFixture media) : PipelineTestBase(media)
{
    private QualityMeasurer Measurer() => new("ffmpeg", "ffprobe");

    [FfmpegFact(filter: "libvmaf")]
    public async Task IdenticalFilesScoreNearlyPerfect()
    {
        var result = await Measurer().MeasureAsync(
            Media.NaturalVideo, Media.NaturalVideo, QualityMetric.Vmaf, new MeasureOptions());

        Assert.True(result.Score > 97, $"a file compared against itself scored only {result.Score:F2}");
    }

    [FfmpegTheory]
    [InlineData(QualityMetric.Xpsnr)]
    [InlineData(QualityMetric.Ssim)]
    [InlineData(QualityMetric.Psnr)]
    public async Task EveryMetricProducesAScore(QualityMetric metric) => await MetricScores(metric);

    /// <summary>
    /// VMAF is its own test because libvmaf is a build-time component that most FFmpeg
    /// packages leave out, while the other three metrics are always present.
    /// </summary>
    [FfmpegFact(filter: "libvmaf")]
    public async Task VmafProducesAScore() => await MetricScores(QualityMetric.Vmaf);

    private async Task MetricScores(QualityMetric metric)
    {
        var encoded = Media.Output($"metric_{metric}.mkv");
        await EncodeAsync(Media.NaturalVideo, encoded, crf: 28);

        var result = await Measurer().MeasureAsync(Media.NaturalVideo, encoded, metric, new MeasureOptions());

        Assert.True(result.Score > 0, $"{metric} returned {result.Score}");
        Assert.Equal(metric, result.Metric);
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task QualityFallsAsCrfRises()
    {
        // The whole premise of target-quality encoding: the metric has to respond
        // monotonically to the setting, or searching over it is meaningless.
        var low = Media.Output("q_crf18.mkv");
        var high = Media.Output("q_crf38.mkv");

        await EncodeAsync(Media.NaturalVideo, low, crf: 18);
        await EncodeAsync(Media.NaturalVideo, high, crf: 38);

        var measurer = Measurer();
        var lowCrf = await measurer.MeasureAsync(Media.NaturalVideo, low, QualityMetric.Vmaf, new MeasureOptions());
        var highCrf = await measurer.MeasureAsync(Media.NaturalVideo, high, QualityMetric.Vmaf, new MeasureOptions());

        Assert.True(lowCrf.Score > highCrf.Score + 10,
            $"CRF 18 scored {lowCrf.Score:F1} and CRF 38 scored {highCrf.Score:F1} — expected a clear gap");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task ShortSampleIsNotPaddedAgainstALongReference()
    {
        // Regression: without framesync's shortest=1 the comparison ran on past the end
        // of the sample, scoring a frozen frame against moving video. One real clip fell
        // from 98 to 31 that way.
        var sample = Media.Output("sample_5s.mkv");
        await EncodeAsync(Media.NaturalVideo, sample, crf: 16, duration: TimeSpan.FromSeconds(4));

        var result = await Measurer().MeasureAsync(
            Media.NaturalVideo, sample, QualityMetric.Vmaf, new MeasureOptions());

        Assert.True(result.Score > 85,
            $"a near-lossless 4s sample scored {result.Score:F1}; padding past its end is the usual cause");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task DownscaledEncodeIsStillComparable()
    {
        var small = Media.Output("downscaled.mkv");
        await EncodeAsync(Media.NaturalVideo, small, crf: 24, extraArgs: "-vf scale=640:360");

        var result = await Measurer().MeasureAsync(
            Media.NaturalVideo, small, QualityMetric.Vmaf, new MeasureOptions { ScaleToReference = true });

        Assert.True(result.Score > 0);
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task VmafReportsPooledStatistics()
    {
        var encoded = Media.Output("pooled.mkv");
        await EncodeAsync(Media.NaturalVideo, encoded, crf: 30);

        var result = await Measurer().MeasureAsync(
            Media.NaturalVideo, encoded, QualityMetric.Vmaf, new MeasureOptions());

        Assert.NotNull(result.Minimum);
        Assert.NotNull(result.HarmonicMean);
        Assert.True(result.FramesMeasured > 0);
        Assert.True(result.Minimum <= result.Score, "the worst frame cannot beat the mean");
        Assert.True(result.HarmonicMean <= result.Score + 0.001, "the harmonic mean cannot exceed the arithmetic one");
    }

    // ---------------- target-quality search ----------------

    [FfmpegFact(filter: "libvmaf")]
    public async Task SearchFindsACrfThatMeetsTheTarget()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo, CancellationToken.None);

        var request = SearchRequest();
        var options = new TargetQualityOptions
        {
            Metric = QualityMetric.Vmaf,
            Target = 92,
            Samples = 2,
            SampleDuration = TimeSpan.FromSeconds(3),
            MaxProbes = 5,
            IgnoreCache = true,
        };

        var search = new TargetQualitySearch(services);
        var result = await search.SearchAsync(request, options, info, CancellationToken.None);

        Assert.True(result.TargetMet, $"search did not reach the target: {result.Summary}");
        Assert.True(result.PredictedScore >= 92, $"chosen CRF predicts only {result.PredictedScore:F2}");
        Assert.NotEmpty(result.Probes);
        Assert.True(result.Probes.Count <= options.MaxProbes);
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task SampledPredictionMatchesTheFullEncode()
    {
        // The entire approach rests on this: if a few short samples don't predict the
        // whole file, the search is just an expensive guess.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo, CancellationToken.None);

        var options = new TargetQualityOptions
        {
            Metric = QualityMetric.Vmaf,
            Target = 92,
            Samples = 2,
            SampleDuration = TimeSpan.FromSeconds(3),
            MaxProbes = 5,
            IgnoreCache = true,
        };

        var search = new TargetQualitySearch(services);
        var result = await search.SearchAsync(SearchRequest(), options, info, CancellationToken.None);

        var full = Media.Output("prediction_check.mkv");
        await EncodeAsync(Media.NaturalVideo, full, result.Crf);

        var actual = await Measurer().MeasureAsync(Media.NaturalVideo, full, QualityMetric.Vmaf, new MeasureOptions());

        Assert.True(Math.Abs(actual.Score - result.PredictedScore) < 5,
            $"predicted {result.PredictedScore:F2} from samples but the full encode scored {actual.Score:F2}");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task HigherTargetsChooseLowerCrf()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo, CancellationToken.None);
        var search = new TargetQualitySearch(services);

        TargetQualityOptions Options(double target) => new()
        {
            Metric = QualityMetric.Vmaf,
            Target = target,
            Samples = 1,
            SampleDuration = TimeSpan.FromSeconds(3),
            MaxProbes = 4,
            IgnoreCache = true,
        };

        var relaxed = await search.SearchAsync(SearchRequest(), Options(85), info, CancellationToken.None);
        var strict = await search.SearchAsync(SearchRequest(), Options(96), info, CancellationToken.None);

        Assert.True(strict.Crf <= relaxed.Crf,
            $"target 96 chose CRF {strict.Crf} but target 85 chose CRF {relaxed.Crf}");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task SearchIsCachedBetweenRuns()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo, CancellationToken.None);

        var options = new TargetQualityOptions
        {
            Metric = QualityMetric.Vmaf,
            Target = 90,
            Samples = 1,
            SampleDuration = TimeSpan.FromSeconds(3),
            MaxProbes = 3,
        };

        var request = SearchRequest();
        var cache = new QualityCache();
        cache.Clear();

        try
        {
            var search = new TargetQualitySearch(services);
            var first = await search.SearchAsync(request, options, info, CancellationToken.None);
            Assert.False(first.FromCache);

            var second = await search.SearchAsync(request, options, info, CancellationToken.None);
            Assert.True(second.FromCache, "the second identical search should have been served from cache");
            Assert.Equal(first.Crf, second.Crf);
        }
        finally
        {
            cache.Clear();
        }
    }

    [FfmpegFact]
    public async Task SilentSourcesEncodeWithoutAnAudioJob()
    {
        // natural.mp4 has no audio at all. Mapping 0:a:0 on it used to fail the encode.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo, CancellationToken.None);

        Assert.Empty(info.AudioStreams);

        var output = Media.Output("silent.mkv");
        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.NaturalVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Quality = 36,
            Preset = VideoPreset.Ultrafast,
        }, info);

        Assert.Empty(plan.AudioJobs);
        Assert.Null(plan.Audio);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal("h264", Ffmpeg.VideoCodec(output));
    }

    [FfmpegFact]
    public async Task AudioOnlyOnASilentSourceIsRejectedClearly()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo, CancellationToken.None);

        var request = new EncodeRequest
        {
            InputFile = Media.NaturalVideo,
            OutputFile = Media.Output("impossible.opus"),
            TempDirectory = Media.TempDir,
            AudioOnly = true,
        };

        var message = EncodeJobBuilder.Validate(request, info);

        Assert.NotNull(message);
        Assert.Contains("no audio", message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- helpers ----------------

    private EncodeRequest SearchRequest() => new()
    {
        InputFile = Media.NaturalVideo,
        OutputFile = Media.Output("search_target.mkv"),
        TempDirectory = Media.TempDir,
        VideoEncoderId = "ffmpeg-x264",
        Preset = VideoPreset.Ultrafast,
    };

    private static async Task EncodeAsync(
        string input, string output, int crf, TimeSpan? duration = null, string? extraArgs = null)
    {
        var trim = duration is { } d ? $"-t {d.TotalSeconds:F2} " : "";
        var extra = extraArgs is null ? "" : extraArgs + " ";
        var args = $"-y -v error -i \"{input}\" {trim}{extra}-c:v libx264 -crf {crf} -preset ultrafast -an \"{output}\"";

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"test encode failed: {stderr}");
    }
}
