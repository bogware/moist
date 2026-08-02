namespace Moist.Core.Encoders.Video;

public sealed class X265Settings : VideoEncoderSettings
{
    /// <summary>
    /// HEVC profile, or null to let x265 pick one that matches the bit depth.
    /// Leave it null: hardcoding "main" makes every 10-bit source fail with
    /// "main profile not supported, internal bit depth 10".
    /// </summary>
    public string? Profile { get; set; }

    public string? Level { get; set; }
    public bool Hdr { get; set; }
    public string? MasterDisplay { get; set; }
    public string? MaxCll { get; set; }
}
