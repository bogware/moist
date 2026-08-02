namespace Moist.Core.Jobs;

/// <summary>
/// Joins encoded chunks back into one stream, without re-encoding.
/// </summary>
public sealed class ConcatJob : Job
{
    public override string JobType => "Concat";

    /// <summary>Chunk files in playback order. Order is the whole point — do not sort elsewhere.</summary>
    public List<string> Inputs { get; set; } = [];

    /// <summary>
    /// Frames the result must contain. Chunked encoding is the one place where a silent
    /// off-by-one loses or duplicates footage, so the count is checked rather than assumed.
    /// </summary>
    public long ExpectedFrames { get; set; }

    /// <summary>Verify the frame count after joining. Only turn this off if you enjoy surprises.</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Used for the verification probe.</summary>
    public string FFprobePath { get; set; } = "ffprobe";

    /// <summary>Total duration, for progress reporting.</summary>
    public double TotalDurationSeconds { get; set; }
}
