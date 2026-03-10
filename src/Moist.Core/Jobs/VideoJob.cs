using Moist.Core.Encoders;
using Moist.Core.Encoders.Video;

namespace Moist.Core.Jobs;

public sealed class VideoJob : Job
{
    public override string JobType => "Video Encode";
    public VideoEncoderSettings Settings { get; set; } = null!;
    public string EncoderId { get; set; } = string.Empty;

    /// <summary>
    /// Optional filter script (AVS/VS/FFmpeg filter complex) to apply before encoding.
    /// </summary>
    public string? FilterScript { get; set; }

    /// <summary>Total frames in source video, used for progress calculation.</summary>
    public long TotalFrames { get; set; }
}
