using Moist.Core.Chunking;
using Moist.Core.Encoders.Video;
using Moist.Core.Quality;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Per-scene optimisation against real encodes.
///
/// The claim being tested is not "smaller files" on its own — it's that quality stops
/// varying scene to scene. A single CRF gives every scene the same *effort*, which means
/// the hard scene comes out worse than the easy one. Fixing that is the point; the size
/// saving follows from no longer over-spending on the easy scenes.
/// </summary>
[Collection("media")]
public sealed class PerSceneIntegrationTests(MediaFixture media) : PipelineTestBase(media)
{
    private EncodeRequest Request(string output) => new()
    {
        InputFile = Media.VariedVideo,
        OutputFile = Media.Output(output),
        TempDirectory = Media.TempDir,
        VideoEncoderId = "ffmpeg-x264",
        Preset = VideoPreset.Ultrafast,
        AudioEncoderId = "ffmpeg-audio-opus",
    };

    private static ChunkingOptions ChunkOptions() => new()
    {
        Mode = ChunkMode.Scene,
        MinChunkDuration = TimeSpan.FromSeconds(1),
        MaxChunkDuration = TimeSpan.FromSeconds(10),
        Resume = false,
    };

    private static PerSceneOptions SceneOptions(double target = 90) => new()
    {
        Metric = QualityMetric.Vmaf,
        Target = target,
        CalibrationScenes = 2,
        ComplexityProbeFrames = 25,
    };

    [FfmpegFact(filter: "libvmaf")]
    public async Task DifferentScenesGetDifferentCrf()
    {
        // VariedVideo runs from a flat colour field to dense fractal detail, so a single
        // CRF cannot be right for all of it.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_spread.mkv");
        var plan = await new ChunkedEncode(services).PlanAsync(request, info, ChunkOptions(), CancellationToken.None);
        Assert.NotNull(plan);
        Assert.True(plan!.Chunks.Count >= 3, $"expected several scenes, got {plan.Chunks.Count}");

        var result = await new PerSceneOptimizer(services).OptimizeAsync(
            request, plan, SceneOptions(), info, CancellationToken.None);

        Assert.Equal(plan.Chunks.Count, result.Assignments.Count);
        Assert.True(result.MaxCrf - result.MinCrf >= 5,
            $"scenes this different should not all get the same CRF (got {result.MinCrf}-{result.MaxCrf})");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task ComplexScenesGetLowerCrfThanSimpleOnes()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_order.mkv");
        var plan = await new ChunkedEncode(services).PlanAsync(request, info, ChunkOptions(), CancellationToken.None);

        var result = await new PerSceneOptimizer(services).OptimizeAsync(
            request, plan!, SceneOptions(), info, CancellationToken.None);

        var withComplexity = result.Assignments.Where(a => a.Complexity > 0).ToList();
        if (withComplexity.Count < 2) return;   // nothing to compare

        var busiest = withComplexity.OrderByDescending(a => a.Complexity).First();
        var quietest = withComplexity.OrderBy(a => a.Complexity).First();

        Assert.True(busiest.Crf <= quietest.Crf,
            $"busiest scene got CRF {busiest.Crf} but the quietest got {quietest.Crf}");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task PerSceneEvensOutQualityAcrossTheFile()
    {
        // The headline claim, measured: every scene should land near the target rather
        // than some sailing past it while others fall short.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_even.mkv");
        var coordinator = new ChunkedEncode(services);
        var plan = await coordinator.PlanAsync(request, info, ChunkOptions(), CancellationToken.None);

        var optimized = await new PerSceneOptimizer(services).OptimizeAsync(
            request, plan!, SceneOptions(88), info, CancellationToken.None);

        var chunks = coordinator.Prepare(request, plan!, ChunkOptions(), optimized.ChunkQuality);
        var jobs = EncodeJobBuilder.Build(request, info, chunks, ffprobePath: "ffprobe").AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var scores = await ScorePerSceneAsync(request.OutputFile, plan!);
        var spread = scores.Max() - scores.Min();

        Assert.True(spread < 25,
            $"per-scene quality still varies by {spread:F1} VMAF across scenes: {string.Join(", ", scores.Select(s => s.ToString("F1")))}");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task PerSceneBeatsAFixedCrfChosenForTheHardestScene()
    {
        // The fair comparison. Judging on mean quality flatters fixed CRF, because the
        // mean hides the scene that looks bad. A careful user picks a CRF low enough for
        // the hardest scene — so that is what per-scene has to beat.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        const double target = 88;

        var perSceneRequest = Request("fair_perscene.mkv");
        var coordinator = new ChunkedEncode(services);
        var plan = await coordinator.PlanAsync(perSceneRequest, info, ChunkOptions(), CancellationToken.None);

        var optimized = await new PerSceneOptimizer(services).OptimizeAsync(
            perSceneRequest, plan!, SceneOptions(target), info, CancellationToken.None);

        var chunks = coordinator.Prepare(perSceneRequest, plan!, ChunkOptions(), optimized.ChunkQuality);
        var perSceneJobs = EncodeJobBuilder.Build(perSceneRequest, info, chunks, ffprobePath: "ffprobe").AllJobs.ToList();
        Assert.True(await RunAsync(services, perSceneJobs), Failure(perSceneJobs));

        // A fixed CRF low enough that no scene falls short: the lowest per-scene picked.
        var fixedRequest = Request("fair_fixed.mkv") with { Quality = optimized.MinCrf };
        var fixedJobs = EncodeJobBuilder.Build(fixedRequest, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, fixedJobs), Failure(fixedJobs));

        var perSceneWorst = (await ScorePerSceneAsync(perSceneRequest.OutputFile, plan!)).Min();
        var fixedWorst = (await ScorePerSceneAsync(fixedRequest.OutputFile, plan!)).Min();

        var perSceneSize = new FileInfo(perSceneRequest.OutputFile).Length;
        var fixedSize = new FileInfo(fixedRequest.OutputFile).Length;

        // Both hold up the worst scene comparably...
        Assert.True(perSceneWorst > fixedWorst - 6,
            $"per-scene worst scene {perSceneWorst:F1} is well below fixed CRF's {fixedWorst:F1}");

        // ...but per-scene should not be spending as much to do it.
        Assert.True(perSceneSize <= fixedSize,
            $"per-scene produced {perSceneSize / 1024}KB against fixed CRF's {fixedSize / 1024}KB " +
            "at comparable worst-case quality");
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task ABadPredictionIsCaughtAndCorrected()
    {
        // Complexity is measured as compressibility, and the two come apart: a test
        // pattern compresses to almost nothing yet its sharp edges fall apart when
        // quantised hard. Predictions are therefore checked, not trusted.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_verify.mkv");
        var plan = await new ChunkedEncode(services).PlanAsync(request, info, ChunkOptions(), CancellationToken.None);

        var verified = await new PerSceneOptimizer(services).OptimizeAsync(
            request, plan!, SceneOptions(92) with { VerifyModelled = true }, info, CancellationToken.None);

        // Every scene either was searched, or was measured and found good enough.
        foreach (var assignment in verified.Assignments)
        {
            Assert.True(assignment.Calibrated || assignment.MeasuredScore is not null,
                $"scene {assignment.ChunkIndex} was neither searched nor checked");
        }
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task FullStrategySearchesEveryScene()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_full.mkv");
        var plan = await new ChunkedEncode(services).PlanAsync(request, info, ChunkOptions(), CancellationToken.None);

        var result = await new PerSceneOptimizer(services).OptimizeAsync(
            request, plan!, SceneOptions() with { Strategy = PerSceneStrategy.Full }, info, CancellationToken.None);

        Assert.Equal(plan!.Chunks.Count, result.CalibratedCount);
        Assert.All(result.Assignments, a => Assert.NotNull(a.MeasuredScore));
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task PerSceneQualityReachesTheChunkJobs()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_applied.mkv");
        var coordinator = new ChunkedEncode(services);
        var plan = await coordinator.PlanAsync(request, info, ChunkOptions(), CancellationToken.None);

        var quality = new Dictionary<int, double>();
        for (var i = 0; i < plan!.Chunks.Count; i++) quality[i] = 20 + i;

        var chunks = coordinator.Prepare(request, plan, ChunkOptions(), quality);
        var built = EncodeJobBuilder.Build(request, info, chunks, ffprobePath: "ffprobe");

        for (var i = 0; i < built.ChunkJobs.Count; i++)
            Assert.Equal(quality[i], built.ChunkJobs[i].Settings.Quality);
    }

    [FfmpegFact(filter: "libvmaf")]
    public async Task DifferentPerSceneAssignmentsDoNotShareChunkFiles()
    {
        // Otherwise a second run at a different target would splice in chunks encoded
        // for the first one.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.VariedVideo, CancellationToken.None);

        var request = Request("perscene_fingerprint.mkv");
        var coordinator = new ChunkedEncode(services);
        var plan = await coordinator.PlanAsync(request, info, ChunkOptions(), CancellationToken.None);

        var first = coordinator.Prepare(request, plan!, ChunkOptions(),
            new Dictionary<int, double> { [0] = 20 });
        var second = coordinator.Prepare(request, plan!, ChunkOptions(),
            new Dictionary<int, double> { [0] = 30 });

        Assert.NotEqual(first.ChunkDirectory, second.ChunkDirectory);
    }

    /// <summary>VMAF of each planned scene, measured against the same span of the source.</summary>
    private async Task<List<double>> ScorePerSceneAsync(string encoded, ChunkPlan plan)
    {
        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");
        var scores = new List<double>();

        foreach (var chunk in plan.Chunks)
        {
            // Cut the same span out of the encode so both sides line up.
            var slice = Media.Output($"slice_{Guid.NewGuid():N}.mkv");
            await ExtractAsync(encoded, chunk.StartTime, chunk.Duration, slice);

            try
            {
                var result = await measurer.MeasureAsync(
                    Media.VariedVideo, slice, QualityMetric.Vmaf,
                    new MeasureOptions { ReferenceStart = chunk.StartTime, ReferenceDuration = chunk.Duration });

                scores.Add(result.Score);
            }
            finally
            {
                try { File.Delete(slice); } catch { /* best effort */ }
            }
        }

        return scores;
    }

    private static async Task ExtractAsync(string input, TimeSpan start, TimeSpan duration, string output)
    {
        var args = $"-y -v error -ss {start.TotalSeconds:F3} -i \"{input}\" -t {duration.TotalSeconds:F3} " +
                   $"-c:v libx264 -crf 8 -preset ultrafast -an \"{output}\"";

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"slice extraction failed: {stderr}");
    }
}
