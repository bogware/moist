namespace Moist.Core.Encoders.Video;

public enum VideoEncodingMode
{
    ConstantQuality,
    ConstantRateFactor,
    SinglePassBitrate,
    TwoPassBitrate
}

public enum VideoPreset
{
    Ultrafast, Superfast, Veryfast, Faster, Fast,
    Medium,
    Slow, Slower, Veryslow, Placebo
}

public enum VideoTune
{
    None, Film, Animation, Grain, StillImage,
    FastDecode, ZeroLatency, Psnr, Ssim
}

/// <summary>
/// Output bit depth. <see cref="Auto"/> follows the source, which is almost always
/// what you want — forcing 8-bit on a 10-bit master throws away precision, and
/// forcing 10-bit on 8-bit material just makes the file bigger.
/// </summary>
public enum VideoBitDepth
{
    Auto,
    Eight,
    Ten,
    Twelve
}

public class VideoEncoderSettings
{
    public VideoEncodingMode Mode { get; set; } = VideoEncodingMode.ConstantRateFactor;
    public int Bitrate { get; set; } = 5000;
    public double Quality { get; set; } = 23.0;
    public VideoPreset Preset { get; set; } = VideoPreset.Medium;
    public VideoTune Tune { get; set; } = VideoTune.None;
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>
    /// Explicit FFmpeg pixel format. Leave null and use <see cref="BitDepth"/> instead
    /// unless you need a specific chroma layout.
    /// </summary>
    public string? PixelFormat { get; set; }

    public VideoBitDepth BitDepth { get; set; } = VideoBitDepth.Auto;

    public double? Framerate { get; set; }
    public int Passes => Mode == VideoEncodingMode.TwoPassBitrate ? 2 : 1;
    public string CustomArgs { get; set; } = string.Empty;

    /// <summary>
    /// Resolve the pixel format to hand FFmpeg, or null to let it decide.
    /// An explicit <see cref="PixelFormat"/> always wins.
    /// </summary>
    public string? ResolvePixelFormat() => PixelFormat ?? BitDepth switch
    {
        VideoBitDepth.Eight => "yuv420p",
        VideoBitDepth.Ten => "yuv420p10le",
        VideoBitDepth.Twelve => "yuv420p12le",
        _ => null,
    };
}
