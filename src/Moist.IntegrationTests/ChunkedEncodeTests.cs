using Moist.Core.Chunking;
using Moist.Core.Encoders.Video;
using Moist.Core.Quality;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Real chunked encodes.
///
/// The thing under test is not speed — it's that splitting a file up, encoding the
/// pieces separately and gluing them back together produces exactly the footage that
/// went in. A gap loses frames, an overlap repeats them, and a misaligned seek shifts
/// everything after it. All three are silent unless something checks.
/// </summary>
[Collection("media")]
public sealed class ChunkedEncodeTests(MediaFixture media) : PipelineTestBase(media)
{
    private static ChunkingOptions Options(ChunkMode mode = ChunkMode.Fixed, double max = 2) => new()
    {
        Mode = mode,
        MinChunkDuration = TimeSpan.FromSeconds(1),
        MaxChunkDuration = TimeSpan.FromSeconds(max),
        Resume = false,
    };

    private EncodeRequest Request(string output) => new()
    {
        InputFile = Media.SceneVideo,
        OutputFile = Media.Output(output),
        TempDirectory = Media.TempDir,
        VideoEncoderId = "ffmpeg-x264",
        Quality = 34,
        Preset = VideoPreset.Ultrafast,
        AudioEncoderId = "ffmpeg-audio-opus",
    };

    [FfmpegFact(frameExactCutting: true)]
    public async Task ChunkedOutputHasExactlyTheSourceFrameCount()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);
        var sourceFrames = await FFmpegConcatProcessor.CountFramesAsync("ffprobe", Media.SceneVideo, CancellationToken.None);

        var coordinator = new ChunkedEncode(services);
        var request = Request("chunked.mkv");
        var chunks = await coordinator.PrepareAsync(request, info, Options(), ct: CancellationToken.None);

        Assert.NotNull(chunks);
        Assert.True(chunks!.Plan.Chunks.Count > 1, "the source should have split into several chunks");
        Assert.Null(chunks.Plan.Validate());

        var plan = EncodeJobBuilder.Build(request, info, chunks, ffprobePath: "ffprobe");
        Assert.True(plan.IsChunked);
        Assert.Equal(chunks.Plan.Chunks.Count, plan.ChunkJobs.Count);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var outputFrames = await FFmpegConcatProcessor.CountFramesAsync(
            "ffprobe", request.OutputFile, CancellationToken.None);

        Assert.Equal(sourceFrames, outputFrames);
    }

    [FfmpegFact(filter: "libvmaf", frameExactCutting: true)]
    public async Task ChunkedOutputMatchesTheSourceContent()
    {
        // A frame count can be right while the content is shifted, so compare the
        // pictures too. Misaligned chunk seeks would tank this.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);

        var request = Request("chunked_quality.mkv");
        var chunks = await new ChunkedEncode(services).PrepareAsync(request, info, Options(), ct: CancellationToken.None);

        var plan = EncodeJobBuilder.Build(request, info, chunks, ffprobePath: "ffprobe");
        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");
        var result = await measurer.MeasureAsync(
            Media.SceneVideo, request.OutputFile, QualityMetric.Vmaf, new MeasureOptions());

        Assert.True(result.Score > 80,
            $"chunked output scored {result.Score:F1} against the source; content is probably shifted");
    }

    [FfmpegFact(filter: "libvmaf", frameExactCutting: true)]
    public async Task ChunkedMatchesSinglePassQuality()
    {
        // Chunking must not cost quality beyond the extra keyframe per boundary.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);

        var single = Request("compare_single.mkv");
        var singlePlan = EncodeJobBuilder.Build(single, info);
        var singleJobs = singlePlan.AllJobs.ToList();
        Assert.True(await RunAsync(services, singleJobs), Failure(singleJobs));

        var chunkedRequest = Request("compare_chunked.mkv");
        var chunks = await new ChunkedEncode(services).PrepareAsync(chunkedRequest, info, Options(), ct: CancellationToken.None);
        var chunkedPlan = EncodeJobBuilder.Build(chunkedRequest, info, chunks, ffprobePath: "ffprobe");
        var chunkedJobs = chunkedPlan.AllJobs.ToList();
        Assert.True(await RunAsync(services, chunkedJobs), Failure(chunkedJobs));

        var measurer = new QualityMeasurer("ffmpeg", "ffprobe");
        var singleScore = await measurer.MeasureAsync(Media.SceneVideo, single.OutputFile, QualityMetric.Vmaf, new MeasureOptions());
        var chunkedScore = await measurer.MeasureAsync(Media.SceneVideo, chunkedRequest.OutputFile, QualityMetric.Vmaf, new MeasureOptions());

        Assert.True(Math.Abs(singleScore.Score - chunkedScore.Score) < 5,
            $"single-pass scored {singleScore.Score:F1} but chunked scored {chunkedScore.Score:F1}");
    }

    [FfmpegFact(frameExactCutting: true)]
    public async Task VerificationCatchesATruncatedChunk()
    {
        // The whole reason the concat verifies rather than trusting FFmpeg's exit code:
        // a short chunk joins perfectly happily and silently loses footage.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);

        var request = Request("truncated.mkv");
        var chunks = await new ChunkedEncode(services).PrepareAsync(request, info, Options(), ct: CancellationToken.None);
        Assert.NotNull(chunks);

        var plan = EncodeJobBuilder.Build(request, info, chunks, ffprobePath: "ffprobe");

        // Encode one chunk deliberately short.
        var victim = plan.ChunkJobs[0];
        victim.TrimFrames = Math.Max(1, victim.TrimFrames!.Value - 3);

        var jobs = plan.AllJobs.ToList();
        var ok = await RunAsync(services, jobs);

        Assert.False(ok, "the concat should have refused a result that is short by 3 frames");

        var concat = plan.Concat!;
        Assert.Equal(Core.Jobs.JobStatus.Failed, concat.Status);
        Assert.Contains("frames", concat.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [FfmpegFact(frameExactCutting: true)]
    public async Task SceneModeCutsWhereTheScenesAre()
    {
        // SceneVideo is three visually distinct segments spliced together, so detection
        // should find the joins.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);

        var detector = new SceneDetector("ffmpeg");
        var cuts = await detector.DetectAsync(
            Media.SceneVideo, info.Video!.Framerate, 10.0, info.Duration, CancellationToken.None);

        Assert.True(cuts.Count >= 2, $"expected at least 2 scene cuts, found {cuts.Count}");

        var plan = ChunkPlanner.Plan(
            (long)Math.Round(info.Duration.TotalSeconds * info.Video.Framerate),
            info.Video.Framerate,
            Options(ChunkMode.Scene, max: 30),
            cuts);

        Assert.Null(plan.Validate());
        Assert.True(plan.Chunks.Count >= 2);
    }

    [FfmpegFact(frameExactCutting: true)]
    public async Task InterruptedRunReusesFinishedChunks()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);

        var options = new ChunkingOptions
        {
            Mode = ChunkMode.Fixed,
            MinChunkDuration = TimeSpan.FromSeconds(1),
            MaxChunkDuration = TimeSpan.FromSeconds(2),
            Resume = true,
        };

        var request = Request("resume.mkv");
        var coordinator = new ChunkedEncode(services);

        var first = await coordinator.PrepareAsync(request, info, options, ct: CancellationToken.None);
        Assert.NotNull(first);
        Assert.Empty(first!.AlreadyDone);

        // Encode just the first two chunks, standing in for an interrupted run.
        var plan = EncodeJobBuilder.Build(request, info, first, ffprobePath: "ffprobe");
        var partial = plan.ChunkJobs.Take(2).Cast<Core.Jobs.Job>().ToList();
        Assert.True(await RunAsync(services, partial), Failure(partial));

        // Re-preparing must recognise them and leave the rest to do.
        var second = await coordinator.PrepareAsync(request, info, options, ct: CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(2, second!.AlreadyDone.Count);
        Assert.Equal(second.Plan.Chunks.Count - 2, second.RemainingCount);
    }

    [FfmpegFact(frameExactCutting: true)]
    public async Task AHalfWrittenChunkIsNotReused()
    {
        // A chunk left behind by a kill is the dangerous case: it exists, so a naive
        // resume would splice it straight in.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);

        var options = new ChunkingOptions
        {
            Mode = ChunkMode.Fixed,
            MinChunkDuration = TimeSpan.FromSeconds(1),
            MaxChunkDuration = TimeSpan.FromSeconds(2),
            Resume = true,
        };

        var request = Request("partial.mkv");
        var coordinator = new ChunkedEncode(services);
        var prep = await coordinator.PrepareAsync(request, info, options, ct: CancellationToken.None);
        Assert.NotNull(prep);

        // Write a plausible but short file where the first chunk belongs.
        var plan = EncodeJobBuilder.Build(request, info, prep, ffprobePath: "ffprobe");
        var victim = plan.ChunkJobs[0];
        victim.TrimFrames = 1;
        Assert.True(await RunAsync(services, [victim]), Failure([victim]));

        var again = await coordinator.PrepareAsync(request, info, options, ct: CancellationToken.None);

        Assert.NotNull(again);
        Assert.DoesNotContain(0, again!.AlreadyDone);
        Assert.False(File.Exists(prep!.ChunkFiles[0]), "the short chunk should have been discarded");
    }

    [FfmpegFact(frameExactCutting: true)]
    public async Task ShortSourcesDeclineChunkingRatherThanSplitting()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var prep = await new ChunkedEncode(services).PrepareAsync(
            new EncodeRequest
            {
                InputFile = Media.SampleVideo,
                OutputFile = Media.Output("tiny.mkv"),
                TempDirectory = Media.TempDir,
            },
            info,
            new ChunkingOptions { MinChunkDuration = TimeSpan.FromSeconds(30) },
            ct: CancellationToken.None);

        Assert.Null(prep);
    }

    [FfmpegFact(frameExactCutting: true)]
    public async Task ChunkedEncodePreservesAudioAndSubtitles()
    {
        // The chunking only applies to video; everything else must still come through.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);

        var request = new EncodeRequest
        {
            InputFile = Media.RichVideo,
            OutputFile = Media.Output("chunked_rich.mkv"),
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Quality = 38,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioTracks = [0, 1, 2],
        };

        var chunks = await new ChunkedEncode(services).PrepareAsync(request, info, Options(), ct: CancellationToken.None);
        Assert.NotNull(chunks);

        var plan = EncodeJobBuilder.Build(request, info, chunks, ffprobePath: "ffprobe");
        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var streams = Ffmpeg.StreamTypes(request.OutputFile);
        Assert.Contains("video", streams);
        Assert.Contains("subtitle", streams);
        Assert.Equal(3, streams.Split("audio").Length - 1);

        // Chapters ride along too.
        Assert.Contains("Opening", Ffmpeg.Probe("-v error -show_chapters -of default=nw=1", request.OutputFile));
    }
}
