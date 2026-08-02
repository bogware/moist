using System.Globalization;

namespace Moist.Core.Chunking;

/// <summary>How chunk boundaries are chosen.</summary>
public enum ChunkMode
{
    /// <summary>
    /// Cut at detected scene changes. Costs an analysis pass, but boundaries land where
    /// the encoder would have placed a keyframe anyway, so nothing is wasted.
    /// </summary>
    Scene,

    /// <summary>
    /// Cut at fixed intervals. No analysis pass, but every boundary forces a keyframe
    /// that wouldn't otherwise exist, which costs a little size.
    /// </summary>
    Fixed,
}

/// <summary>
/// One span of the source, measured in frames rather than time.
///
/// Frames, not timestamps, are the unit here on purpose: at 23.976 fps a duration in
/// seconds cannot address a frame boundary exactly, and the rounding error accumulates
/// across chunks until the concatenated result has gained or lost frames.
/// </summary>
public sealed record VideoChunk(int Index, long StartFrame, long FrameCount, double Framerate)
{
    public long EndFrameExclusive => StartFrame + FrameCount;

    public TimeSpan StartTime => TimeSpan.FromSeconds(StartFrame / Framerate);
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / Framerate);

    /// <summary>Seek position for FFmpeg, with enough precision to land on the right frame.</summary>
    public string SeekArgument => (StartFrame / Framerate).ToString("F6", CultureInfo.InvariantCulture);

    public override string ToString()
        => $"#{Index} frames {StartFrame}-{EndFrameExclusive - 1} ({FrameCount} @ {StartTime:hh\\:mm\\:ss})";
}

/// <summary>A complete tiling of the source into chunks.</summary>
public sealed class ChunkPlan
{
    public required IReadOnlyList<VideoChunk> Chunks { get; init; }
    public required long TotalFrames { get; init; }
    public required double Framerate { get; init; }
    public ChunkMode Mode { get; init; }

    /// <summary>Scene cuts found by detection, for reporting. Empty in fixed mode.</summary>
    public IReadOnlyList<long> SceneCuts { get; init; } = [];

    public TimeSpan Duration => TimeSpan.FromSeconds(TotalFrames / Framerate);

    /// <summary>
    /// The chunks must tile the source exactly: no gaps, no overlaps, nothing missing at
    /// either end. A plan that fails this would silently drop or duplicate footage.
    /// </summary>
    public string? Validate()
    {
        if (Chunks.Count == 0) return "the plan contains no chunks";

        long expected = 0;
        for (var i = 0; i < Chunks.Count; i++)
        {
            var chunk = Chunks[i];
            if (chunk.FrameCount <= 0)
                return $"chunk {i} covers {chunk.FrameCount} frames";
            if (chunk.StartFrame != expected)
                return $"chunk {i} starts at frame {chunk.StartFrame} but the previous chunk ended at {expected}";
            expected = chunk.EndFrameExclusive;
        }

        if (expected != TotalFrames)
            return $"chunks cover {expected} frames but the source has {TotalFrames}";

        return null;
    }
}
