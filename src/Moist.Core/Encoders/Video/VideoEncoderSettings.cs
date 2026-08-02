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

/// <summary>
/// Chroma sampling. <see cref="Auto"/> follows the source, which is what you want unless
/// you are mastering — throwing away chroma resolution is not recoverable, and adding it
/// only makes the file bigger.
/// </summary>
public enum ChromaSubsampling
{
    Auto,

    /// <summary>4:2:0. What virtually all delivery formats use.</summary>
    Yuv420,

    /// <summary>4:2:2. Broadcast and intermediate codecs.</summary>
    Yuv422,

    /// <summary>4:4:4. No chroma subsampling; screen capture and mastering.</summary>
    Yuv444,
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

    public ChromaSubsampling Chroma { get; set; } = ChromaSubsampling.Auto;

    public double? Framerate { get; set; }
    public int Passes => Mode == VideoEncodingMode.TwoPassBitrate ? 2 : 1;
    public string CustomArgs { get; set; } = string.Empty;

    /// <summary>
    /// Resolve the pixel format to hand FFmpeg, or null to let it decide.
    /// An explicit <see cref="PixelFormat"/> always wins.
    /// </summary>
    /// <param name="sourceBitDepth">
    /// Source depth, used when only the chroma layout was specified — asking for 4:4:4
    /// shouldn't silently drop a 10-bit master to 8-bit.
    /// </param>
    public string? ResolvePixelFormat(int sourceBitDepth = 8)
    {
        if (PixelFormat is not null) return PixelFormat;

        // Nothing asked for: let the encoder follow the source exactly.
        if (BitDepth == VideoBitDepth.Auto && Chroma == ChromaSubsampling.Auto) return null;

        var depth = BitDepth switch
        {
            VideoBitDepth.Eight => 8,
            VideoBitDepth.Ten => 10,
            VideoBitDepth.Twelve => 12,
            _ => sourceBitDepth is 8 or 10 or 12 ? sourceBitDepth : 8,
        };

        var chroma = Chroma switch
        {
            ChromaSubsampling.Yuv422 => "yuv422p",
            ChromaSubsampling.Yuv444 => "yuv444p",
            _ => "yuv420p",
        };

        // 8-bit formats carry no depth suffix or endianness.
        return depth == 8 ? chroma : $"{chroma}{depth}le";
    }
}
