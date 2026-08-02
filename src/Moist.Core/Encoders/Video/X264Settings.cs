namespace Moist.Core.Encoders.Video;

public sealed class X264Settings : VideoEncoderSettings
{
    /// <summary>
    /// H.264 profile, or null to let x264 pick one that matches the bit depth.
    /// Leave it null: hardcoding "high" makes every 10-bit source fail with
    /// "high profile doesn't support a bit depth of 10".
    /// </summary>
    public string? Profile { get; set; }

    public string? Level { get; set; }
    public int RefFrames { get; set; } = 3;
    public int BFrames { get; set; } = 3;
    public bool Cabac { get; set; } = true;
    public int? Threads { get; set; }
    public bool BlurayCompat { get; set; }
}
