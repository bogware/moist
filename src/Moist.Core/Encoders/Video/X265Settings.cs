namespace Moist.Core.Encoders.Video;

public sealed class X265Settings : VideoEncoderSettings
{
    public string Profile { get; set; } = "main";
    public string? Level { get; set; }
    public int BitDepth { get; set; } = 10;
    public bool Hdr { get; set; }
    public string? MasterDisplay { get; set; }
    public string? MaxCll { get; set; }
}
