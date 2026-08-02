using Moist.Core.Chunking;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

/// <summary>
/// Chunk planning arithmetic. Everything here is about one property: the chunks must
/// tile the source exactly, because a gap loses footage and an overlap repeats it.
/// </summary>
public class ChunkingTests
{
    private static ChunkingOptions Options(double min = 5, double max = 60, ChunkMode mode = ChunkMode.Scene) => new()
    {
        Mode = mode,
        MinChunkDuration = TimeSpan.FromSeconds(min),
        MaxChunkDuration = TimeSpan.FromSeconds(max),
    };

    // ---------------- the tiling property ----------------

    [Theory]
    [InlineData(1200, 25)]
    [InlineData(150_000, 23.976)]
    [InlineData(7, 25)]
    [InlineData(99_991, 29.97)]
    public void ChunksTileTheSourceExactly_Fixed(long totalFrames, double fps)
    {
        var plan = ChunkPlanner.Plan(totalFrames, fps, Options(mode: ChunkMode.Fixed));

        Assert.Null(plan.Validate());
        Assert.Equal(totalFrames, plan.Chunks.Sum(c => c.FrameCount));
        Assert.Equal(0, plan.Chunks[0].StartFrame);
        Assert.Equal(totalFrames, plan.Chunks[^1].EndFrameExclusive);
    }

    [Fact]
    public void ChunksTileTheSourceExactly_WithSceneCuts()
    {
        // Deliberately awkward: cuts too close together, out of order, duplicated,
        // and some outside the file.
        long[] cuts = [300, 100, 100, 5, 12_000, 850, 851, 852, -4, 1200];

        var plan = ChunkPlanner.Plan(10_000, 25, Options(), cuts);

        Assert.Null(plan.Validate());
        Assert.Equal(10_000, plan.Chunks.Sum(c => c.FrameCount));
    }

    [Fact]
    public void NonIntegerFramerateDoesNotDriftAcrossChunks()
    {
        // Frames, not seconds, is the unit precisely because 1/23.976 cannot address a
        // frame boundary. If the planner worked in seconds this would come up short.
        var plan = ChunkPlanner.Plan(172_800, 23.976, Options(max: 30, mode: ChunkMode.Fixed));

        Assert.Null(plan.Validate());
        Assert.Equal(172_800, plan.Chunks.Sum(c => c.FrameCount));
    }

    [Fact]
    public void AMalformedPlanIsRejectedRatherThanReturned()
    {
        var broken = new ChunkPlan
        {
            Chunks = [new VideoChunk(0, 0, 100, 25), new VideoChunk(1, 150, 100, 25)],  // gap
            TotalFrames = 250,
            Framerate = 25,
        };

        Assert.NotNull(broken.Validate());
        Assert.Contains("previous chunk ended", broken.Validate()!);
    }

    [Fact]
    public void OverlappingChunksAreCaught()
    {
        var overlapping = new ChunkPlan
        {
            Chunks = [new VideoChunk(0, 0, 100, 25), new VideoChunk(1, 50, 100, 25)],
            TotalFrames = 150,
            Framerate = 25,
        };

        Assert.NotNull(overlapping.Validate());
    }

    [Fact]
    public void ShortfallAgainstTheSourceIsCaught()
    {
        var short_ = new ChunkPlan
        {
            Chunks = [new VideoChunk(0, 0, 100, 25)],
            TotalFrames = 200,
            Framerate = 25,
        };

        Assert.Contains("but the source has 200", short_.Validate()!);
    }

    // ---------------- boundary quality ----------------

    [Fact]
    public void SceneCutsBecomeChunkBoundaries()
    {
        // Cuts spaced comfortably beyond the minimum should each start a chunk.
        long[] cuts = [250, 500, 750];

        var plan = ChunkPlanner.Plan(1000, 25, Options(min: 5, max: 60), cuts);

        var starts = plan.Chunks.Select(c => c.StartFrame).ToList();
        Assert.Equal(new long[] { 0, 250, 500, 750 }, starts);
    }

    [Fact]
    public void CutsTooCloseTogetherAreAbsorbed()
    {
        // A chunk that is mostly keyframe compresses badly and buys no parallelism.
        long[] cuts = [10, 20, 30, 40, 500];

        var plan = ChunkPlanner.Plan(1000, 25, Options(min: 8), cuts);

        Assert.All(plan.Chunks, c => Assert.True(c.FrameCount >= 100 || c.Index == plan.Chunks.Count - 1,
            $"chunk {c.Index} is only {c.FrameCount} frames"));
        Assert.Null(plan.Validate());
    }

    [Fact]
    public void LongScenesAreSplitToKeepParallelism()
    {
        // One 40-second scene would otherwise become the floor on wall-clock time.
        var plan = ChunkPlanner.Plan(1000, 25, Options(min: 2, max: 10), sceneCuts: []);

        Assert.True(plan.Chunks.Count > 1, "a long span should have been split");
        Assert.All(plan.Chunks, c => Assert.True(c.FrameCount <= 250 + 1,
            $"chunk {c.Index} runs {c.FrameCount} frames, past the cap"));
    }

    [Fact]
    public void ATinyTailIsFoldedIntoThePrecedingChunk()
    {
        // 1001 frames with a 500-frame cap would otherwise leave a 1-frame chunk.
        var plan = ChunkPlanner.Plan(1001, 25, Options(max: 20, mode: ChunkMode.Fixed));

        Assert.All(plan.Chunks, c => Assert.True(c.FrameCount > 1, "a one-frame chunk survived"));
        Assert.Null(plan.Validate());
    }

    // ---------------- worthwhileness ----------------

    [Fact]
    public void VeryShortSourcesAreNotWorthChunking()
    {
        // Splitting a 6-second clip costs more in forced keyframes than it saves.
        Assert.False(ChunkPlanner.IsWorthwhile(150, 25, Options(min: 5)));
        Assert.True(ChunkPlanner.IsWorthwhile(25_000, 25, Options(min: 5)));
    }

    [Fact]
    public void NonsenseInputIsNotWorthChunking()
    {
        Assert.False(ChunkPlanner.IsWorthwhile(0, 25, Options()));
        Assert.False(ChunkPlanner.IsWorthwhile(1000, 0, Options()));
    }

    // ---------------- chunk addressing ----------------

    [Fact]
    public void SeekPositionCarriesEnoughPrecisionForOddFramerates()
    {
        var chunk = new VideoChunk(1, 1000, 500, 23.976);

        // 1000/23.976 = 41.7084..., and three decimals would land on the wrong frame.
        Assert.StartsWith("41.708", chunk.SeekArgument);
    }

    [Fact]
    public void ChunkKnowsItsOwnExtent()
    {
        var chunk = new VideoChunk(2, 500, 250, 25);

        Assert.Equal(750, chunk.EndFrameExclusive);
        Assert.Equal(TimeSpan.FromSeconds(20), chunk.StartTime);
        Assert.Equal(TimeSpan.FromSeconds(10), chunk.Duration);
    }

    // ---------------- concat list ----------------

    [Fact]
    public void ConcatListQuotesPathsTheWayTheDemuxerExpects()
    {
        var dir = Path.GetTempPath();
        var job = new ConcatJob
        {
            Name = "t",
            InputFile = "in.mkv",
            OutputFile = Path.Combine(dir, $"moist_test_{Guid.NewGuid():N}.mkv"),
            Inputs = [Path.Combine(dir, "a.mkv"), Path.Combine(dir, "o'brien clip.mkv")],
        };

        var listFile = FFmpegConcatProcessor.WriteListFile(job);
        try
        {
            var text = File.ReadAllText(listFile);

            Assert.Contains("file '", text);
            Assert.Contains("a.mkv'", text);
            // Separators are normalised to forward slashes for the demuxer. (The only
            // backslash left should be the apostrophe escape below.)
            Assert.DoesNotContain(@"\\", text);
            Assert.DoesNotContain(@"C:\", text);
            // The demuxer's escape for an apostrophe is close-escape-reopen.
            Assert.Contains(@"'\''", text);
        }
        finally
        {
            File.Delete(listFile);
        }
    }

    [Fact]
    public void ConcatListPreservesOrder()
    {
        // Sorting these would silently reorder the film.
        var job = new ConcatJob
        {
            Name = "t",
            InputFile = "in.mkv",
            OutputFile = Path.Combine(Path.GetTempPath(), $"moist_test_{Guid.NewGuid():N}.mkv"),
            Inputs = ["/tmp/chunk_00010.mkv", "/tmp/chunk_00002.mkv", "/tmp/chunk_00001.mkv"],
        };

        var listFile = FFmpegConcatProcessor.WriteListFile(job);
        try
        {
            var lines = File.ReadAllLines(listFile);
            Assert.Contains("00010", lines[0]);
            Assert.Contains("00002", lines[1]);
            Assert.Contains("00001", lines[2]);
        }
        finally
        {
            File.Delete(listFile);
        }
    }
}
