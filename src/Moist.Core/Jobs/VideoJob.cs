using Moist.Core.Encoders;
using Moist.Core.Encoders.Video;

namespace Moist.Core.Jobs;

public sealed class VideoJob : Job
{
    public override string JobType => "Video Encode";
    public VideoEncoderSettings Settings { get; set; } = null!;
    public string EncoderId { get; set; } = string.Empty;

    /// <summary>
    /// Optional filter script to apply before encoding.
    /// For FFmpeg engine: a simple filter chain string (e.g. "scale=1920:1080,hqdn3d=4:3:6").
    /// For VapourSynth/AviSynth: a full script to be written to a temp file.
    /// </summary>
    public string? FilterScript { get; set; }

    /// <summary>
    /// Identifies which filter engine produced the FilterScript.
    /// "ffmpeg" = simple -vf chain, "vapoursynth" = .vpy script, "avisynth" = .avs script.
    /// </summary>
    public string? FilterEngineId { get; set; }

    /// <summary>Total frames in source video, used for progress calculation.</summary>
    public long TotalFrames { get; set; }

    /// <summary>
    /// Source bit depth, so a request for a chroma layout alone doesn't accidentally
    /// drop a 10-bit master to 8-bit.
    /// </summary>
    public int SourceBitDepth { get; set; } = 8;

    /// <summary>
    /// Encode only from this point in the source. Emitted as <c>-ss</c> before the input,
    /// which is both fast and frame-aligned with a measurement pass that seeks the same way.
    /// </summary>
    public TimeSpan? TrimStart { get; set; }

    /// <summary>Encode only this much of the source, from <see cref="TrimStart"/>.</summary>
    public TimeSpan? TrimDuration { get; set; }

    /// <summary>
    /// Encode exactly this many frames, emitted as <c>-frames:v</c>.
    /// Chunked encoding relies on this: a duration in seconds cannot address a frame
    /// boundary exactly at 23.976 fps, and the rounding error accumulates across chunks
    /// until the joined result has gained or lost frames. A frame count cannot drift.
    /// Takes precedence over <see cref="TrimDuration"/>.
    /// </summary>
    public long? TrimFrames { get; set; }
}
