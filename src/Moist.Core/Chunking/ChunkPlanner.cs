namespace Moist.Core.Chunking;

public sealed class ChunkingOptions
{
    public ChunkMode Mode { get; init; } = ChunkMode.Scene;

    /// <summary>
    /// Chunks shorter than this compress badly — every chunk starts with a keyframe, and
    /// too many keyframes is wasted bitrate — and add scheduling overhead for little gain.
    /// </summary>
    public TimeSpan MinChunkDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Long chunks limit parallelism: the whole encode can't finish before its slowest
    /// chunk, so one very long chunk becomes the floor on wall-clock time.
    /// </summary>
    public TimeSpan MaxChunkDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>scdet sensitivity, 0-100. Lower finds more cuts.</summary>
    public double SceneThreshold { get; init; } = 10.0;

    /// <summary>Reuse chunk files left behind by an interrupted run.</summary>
    public bool Resume { get; init; } = true;

    /// <summary>Check the concatenated result frame-for-frame against the plan.</summary>
    public bool Verify { get; init; } = true;
}

/// <summary>
/// Turns scene cuts into a chunk plan.
///
/// The competing pressures are simple: more chunks means more parallelism but more
/// forced keyframes and more overhead, and the encode can never finish faster than its
/// longest chunk. So chunks are grown to at least the minimum duration and split when
/// they would exceed the maximum.
/// </summary>
public static class ChunkPlanner
{
    /// <param name="sceneCuts">Frame numbers where a new scene begins. Need not be sorted.</param>
    public static ChunkPlan Plan(
        long totalFrames, double framerate, ChunkingOptions options, IReadOnlyList<long>? sceneCuts = null)
    {
        if (totalFrames <= 0) throw new ArgumentOutOfRangeException(nameof(totalFrames));
        if (framerate <= 0) throw new ArgumentOutOfRangeException(nameof(framerate));

        var minFrames = Math.Max(1, (long)Math.Round(options.MinChunkDuration.TotalSeconds * framerate));
        var maxFrames = Math.Max(minFrames, (long)Math.Round(options.MaxChunkDuration.TotalSeconds * framerate));

        var boundaries = options.Mode == ChunkMode.Scene && sceneCuts is { Count: > 0 }
            ? BoundariesFromScenes(sceneCuts, totalFrames, minFrames, maxFrames)
            : FixedBoundaries(totalFrames, maxFrames);

        var chunks = new List<VideoChunk>(boundaries.Count);
        for (var i = 0; i < boundaries.Count; i++)
        {
            var start = boundaries[i];
            var end = i + 1 < boundaries.Count ? boundaries[i + 1] : totalFrames;
            chunks.Add(new VideoChunk(i, start, end - start, framerate));
        }

        var plan = new ChunkPlan
        {
            Chunks = chunks,
            TotalFrames = totalFrames,
            Framerate = framerate,
            Mode = options.Mode,
            SceneCuts = sceneCuts ?? [],
        };

        // A malformed plan silently drops or duplicates footage, so refuse to hand one back.
        if (plan.Validate() is { } problem)
            throw new InvalidOperationException($"chunk plan is invalid: {problem}");

        return plan;
    }

    private static List<long> BoundariesFromScenes(
        IReadOnlyList<long> sceneCuts, long totalFrames, long minFrames, long maxFrames)
    {
        var cuts = sceneCuts
            .Where(f => f > 0 && f < totalFrames)
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        var boundaries = new List<long> { 0 };

        foreach (var cut in cuts)
        {
            var lastBoundary = boundaries[^1];

            // Too soon after the previous boundary: absorb this scene rather than
            // creating a chunk that's mostly keyframe.
            if (cut - lastBoundary < minFrames) continue;

            boundaries.Add(cut);
        }

        // Split anything that ended up longer than the cap — including the final span,
        // which has no scene cut to close it.
        var capped = new List<long>();
        for (var i = 0; i < boundaries.Count; i++)
        {
            var start = boundaries[i];
            var end = i + 1 < boundaries.Count ? boundaries[i + 1] : totalFrames;
            capped.AddRange(SplitSpan(start, end, maxFrames));
        }

        return MergeTrailingRunt(capped, totalFrames, minFrames);
    }

    private static List<long> FixedBoundaries(long totalFrames, long maxFrames)
    {
        var boundaries = new List<long>();
        for (long start = 0; start < totalFrames; start += maxFrames)
            boundaries.Add(start);

        return MergeTrailingRunt(boundaries, totalFrames, maxFrames / 2);
    }

    /// <summary>Break [start, end) into pieces no longer than the cap, as evenly as possible.</summary>
    private static IEnumerable<long> SplitSpan(long start, long end, long maxFrames)
    {
        var length = end - start;
        if (length <= maxFrames) return [start];

        // Even pieces beat "max, max, …, remainder", which can leave a tiny tail.
        var pieces = (long)Math.Ceiling((double)length / maxFrames);
        var size = (long)Math.Ceiling((double)length / pieces);

        var result = new List<long>();
        for (var offset = start; offset < end; offset += size)
            result.Add(offset);

        return result;
    }

    /// <summary>
    /// Fold away a final chunk that came out very short. A two-frame tail encodes badly
    /// and buys no parallelism.
    /// </summary>
    private static List<long> MergeTrailingRunt(List<long> boundaries, long totalFrames, long minFrames)
    {
        if (boundaries.Count > 1 && totalFrames - boundaries[^1] < minFrames)
            boundaries.RemoveAt(boundaries.Count - 1);

        return boundaries;
    }

    /// <summary>
    /// Is chunking actually worth it here? Splitting a short clip costs more in overhead
    /// and forced keyframes than it saves.
    /// </summary>
    public static bool IsWorthwhile(long totalFrames, double framerate, ChunkingOptions options)
    {
        if (totalFrames <= 0 || framerate <= 0) return false;

        var duration = totalFrames / framerate;
        return duration >= options.MinChunkDuration.TotalSeconds * 2;
    }
}
