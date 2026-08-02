using Moist.Core.Chunking;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Queue persistence.
///
/// The previous implementation stored a flattened snapshot of each job — id, name, input,
/// output — which cannot be turned back into anything runnable, because the encoder
/// settings weren't in it. These tests exist to pin the property that was missing: what
/// is stored has to be enough to rebuild the encode.
/// </summary>
[Collection("media")]
public sealed class QueuePersistenceTests(MediaFixture media) : PipelineTestBase(media), IDisposable
{
    private readonly EncodeQueueStore _store = new();

    public void Dispose() => _store.Clear();

    /// <summary>
    /// A file that exists regardless of whether FFmpeg is installed. The store drops
    /// entries whose source has gone away, so the pure store tests need *something* real
    /// to point at — but they don't need it to be decodable, and they shouldn't be
    /// skipped in CI just because FFmpeg is absent.
    /// </summary>
    private string PlaceholderSource()
    {
        var path = Path.Combine(Media.Root, "placeholder-source.bin");
        if (!File.Exists(path)) File.WriteAllText(path, "not really a video");
        return path;
    }

    /// <summary>For the tests that really encode; these are [FfmpegFact] anyway.</summary>
    private EncodeRequest MediaRequest(string output) => Request(output) with { InputFile = Media.SceneVideo };

    private EncodeRequest Request(string output) => new()
    {
        InputFile = PlaceholderSource(),
        OutputFile = Media.Output(output),
        TempDirectory = Media.TempDir,
        VideoEncoderId = "ffmpeg-x265",
        Quality = 31,
        Preset = VideoPreset.Ultrafast,
        Tune = VideoTune.Animation,
        AudioEncoderId = "ffmpeg-audio-flac",
        AudioTracks = [0],
        BitDepth = VideoBitDepth.Ten,
    };

    [Fact]
    public void StoredEncodeKeepsEverythingNeededToRebuildIt()
    {
        _store.Clear();
        var request = Request("persisted.mkv");

        _store.Record(request, "job-1");
        var loaded = _store.LoadPending();

        var entry = Assert.Single(loaded);

        // Every setting that changes the output has to survive the round trip, or the
        // restored encode isn't the one that was queued.
        Assert.Equal(request.VideoEncoderId, entry.Request.VideoEncoderId);
        Assert.Equal(request.Quality, entry.Request.Quality);
        Assert.Equal(request.Preset, entry.Request.Preset);
        Assert.Equal(request.Tune, entry.Request.Tune);
        Assert.Equal(request.BitDepth, entry.Request.BitDepth);
        Assert.Equal(request.AudioEncoderId, entry.Request.AudioEncoderId);
        Assert.Equal(request.OutputFile, entry.Request.OutputFile);
    }

    [Fact]
    public void FinishingAnEncodeClearsItsEntry()
    {
        _store.Clear();
        _store.Record(Request("done.mkv"), "job-final");

        Assert.Single(_store.LoadPending());

        _store.NotifyJobFinished("job-final");

        Assert.Empty(_store.LoadPending());
    }

    [Fact]
    public void UnrelatedJobsFinishingChangeNothing()
    {
        _store.Clear();
        _store.Record(Request("keep.mkv"), "job-final");

        _store.NotifyJobFinished("some-other-job");

        Assert.Single(_store.LoadPending());
    }

    [Fact]
    public void ReadingThePendingListDoesNotConsumeIt()
    {
        // The old implementation deleted the file on read, so a crash during restore
        // lost the queue permanently.
        _store.Clear();
        _store.Record(Request("twice.mkv"), "job-1");

        Assert.Single(_store.LoadPending());
        Assert.Single(_store.LoadPending());
    }

    [Fact]
    public void EncodesWhoseSourceVanishedAreDropped()
    {
        _store.Clear();
        _store.Record(Request("gone.mkv") with { InputFile = Path.Combine(Media.Root, "not-here.mkv") }, "job-1");

        Assert.Empty(_store.LoadPending());
    }

    [Fact]
    public void SeveralEncodesArePersistedIndependently()
    {
        _store.Clear();
        _store.Record(Request("one.mkv"), "job-1");
        _store.Record(Request("two.mkv"), "job-2");
        _store.Record(Request("three.mkv"), "job-3");

        Assert.Equal(3, _store.LoadPending().Count);

        _store.NotifyJobFinished("job-2");

        var left = _store.LoadPending();
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, e => e.Request.OutputFile.EndsWith("two.mkv"));
    }

    [Fact]
    public void ChunkingSettingsSurviveSoARestoredRunSplitsTheSameWay()
    {
        _store.Clear();
        var chunking = new ChunkingOptions
        {
            Mode = ChunkMode.Fixed,
            MaxChunkDuration = TimeSpan.FromSeconds(17),
            MinChunkDuration = TimeSpan.FromSeconds(3),
        };

        _store.Record(Request("chunked.mkv"), "job-1", chunking,
            new Dictionary<int, double> { [0] = 22, [1] = 28 });

        var entry = Assert.Single(_store.LoadPending());

        Assert.NotNull(entry.Chunking);
        Assert.Equal(ChunkMode.Fixed, entry.Chunking!.Mode);
        Assert.Equal(TimeSpan.FromSeconds(17), entry.Chunking.MaxChunkDuration);
        Assert.Equal(22, entry.PerChunkQuality![0]);
        Assert.Equal(28, entry.PerChunkQuality[1]);
    }

    // ---------------- actually restoring ----------------

    [FfmpegFact]
    public async Task ARecordedEncodeComesBackAsRunnableJobs()
    {
        _store.Clear();
        using var services = CreateServices();
        services.QueueStore.Clear();

        var request = MediaRequest("restored.mkv");
        services.QueueStore.Record(request, "old-job-id");

        var restorer = new EncodeQueueRestorer(services);
        var results = await restorer.RestoreAsync();

        var restored = Assert.Single(results);
        Assert.True(restored.Restored, restored.Reason);
        Assert.NotEmpty(restored.Jobs);

        // And they must actually run, not merely exist.
        Assert.True(await RunAsync(services, restored.Jobs.ToList()), Failure(restored.Jobs));
        Assert.True(File.Exists(request.OutputFile));

        services.QueueStore.Clear();
    }

    [FfmpegFact]
    public async Task RestoredJobsAreQueuedButNotStarted()
    {
        // Coming back to a machine already grinding through work nobody asked for would
        // be worse than losing the queue.
        _store.Clear();
        using var services = CreateServices();
        services.QueueStore.Clear();

        services.QueueStore.Record(MediaRequest("queued_only.mkv"), "old-job");

        var results = await new EncodeQueueRestorer(services).RestoreAsync();

        Assert.True(results[0].Restored);
        Assert.All(results[0].Jobs, j => Assert.Equal(JobStatus.Queued, j.Status));
        Assert.False(services.JobQueue.IsRunning);

        services.QueueStore.Clear();
    }

    [FfmpegFact]
    public async Task AnEncodeThatAlreadyFinishedIsNotRepeated()
    {
        _store.Clear();
        using var services = CreateServices();
        services.QueueStore.Clear();

        var request = Request("already_done.mkv");
        await File.WriteAllTextAsync(request.OutputFile, "pretend this is the finished encode");

        services.QueueStore.Record(request, "old-job");

        var results = await new EncodeQueueRestorer(services).RestoreAsync();

        Assert.False(results[0].Restored);
        Assert.Contains("already finished", results[0].Reason);
        Assert.Empty(services.QueueStore.LoadPending());
    }

    [FfmpegFact]
    public async Task ARestoredChunkedEncodePicksUpFinishedChunks()
    {
        // This is the case that matters: a long chunked encode killed part way should
        // not start again from nothing.
        _store.Clear();
        using var services = CreateServices();
        services.QueueStore.Clear();

        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SceneVideo, CancellationToken.None);
        var chunking = new ChunkingOptions
        {
            Mode = ChunkMode.Fixed,
            MinChunkDuration = TimeSpan.FromSeconds(1),
            MaxChunkDuration = TimeSpan.FromSeconds(2),
            Resume = true,
        };

        var request = MediaRequest("chunk_restore.mkv");
        var coordinator = new ChunkedEncode(services);
        var plan = await coordinator.PlanAsync(request, info, chunking, CancellationToken.None);
        var prep = coordinator.Prepare(request, plan!, chunking);

        // Encode two chunks, then pretend the process died.
        var built = EncodeJobBuilder.Build(request, info, prep, ffprobePath: "ffprobe");
        var partial = built.ChunkJobs.Take(2).Cast<Job>().ToList();
        Assert.True(await RunAsync(services, partial), Failure(partial));

        services.QueueStore.Record(request, "old-job", chunking);

        var results = await new EncodeQueueRestorer(services).RestoreAsync();

        Assert.True(results[0].Restored, results[0].Reason);
        Assert.Equal(2, results[0].ReusedChunks);

        services.QueueStore.Clear();
    }

    [FfmpegFact]
    public async Task RestoringTracksTheNewJobSoTheEntryStillClears()
    {
        // Job ids are regenerated on rebuild, so the store has to start watching the new
        // final job or the entry would linger forever.
        _store.Clear();
        using var services = CreateServices();
        services.QueueStore.Clear();

        var request = MediaRequest("retracked.mkv");
        services.QueueStore.Record(request, "stale-job-id");

        var results = await new EncodeQueueRestorer(services).RestoreAsync();
        Assert.True(results[0].Restored);
        Assert.Single(services.QueueStore.LoadPending());

        Assert.True(await RunAsync(services, results[0].Jobs.ToList()), Failure(results[0].Jobs));

        Assert.Empty(services.QueueStore.LoadPending());
    }
}
