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

public class VideoEncoderSettings
{
    public VideoEncodingMode Mode { get; set; } = VideoEncodingMode.ConstantRateFactor;
    public int Bitrate { get; set; } = 5000;
    public double Quality { get; set; } = 23.0;
    public VideoPreset Preset { get; set; } = VideoPreset.Medium;
    public VideoTune Tune { get; set; } = VideoTune.None;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? PixelFormat { get; set; }
    public double? Framerate { get; set; }
    public int Passes => Mode == VideoEncodingMode.TwoPassBitrate ? 2 : 1;
    public string CustomArgs { get; set; } = string.Empty;
}
