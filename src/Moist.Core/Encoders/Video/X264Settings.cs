namespace Moist.Core.Encoders.Video;

public sealed class X264Settings : VideoEncoderSettings
{
    public string Profile { get; set; } = "high";
    public string? Level { get; set; }
    public int RefFrames { get; set; } = 3;
    public int BFrames { get; set; } = 3;
    public bool Cabac { get; set; } = true;
    public int? Threads { get; set; }
    public bool BlurayCompat { get; set; }
}
