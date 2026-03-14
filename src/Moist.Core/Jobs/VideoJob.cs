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
}
