namespace Moist.Core.Encoders.Audio;

public enum AudioBitrateMode
{
    Vbr,
    Cbr,
    ConstrainedVbr
}

public enum AudioChannelMode
{
    KeepOriginal,
    Mono,
    Stereo,
    Surround51,
    Surround71
}

public class AudioEncoderSettings
{
    public AudioBitrateMode BitrateMode { get; set; } = AudioBitrateMode.Vbr;
    public int Bitrate { get; set; } = 128;
    public double VbrQuality { get; set; } = 5.0;
    public int? SampleRate { get; set; }
    public AudioChannelMode ChannelMode { get; set; } = AudioChannelMode.KeepOriginal;
    public string CustomArgs { get; set; } = string.Empty;
}
