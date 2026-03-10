namespace Moist.Core.Encoders.Audio;

public enum AacEncoder
{
    FfmpegAac,
    Fdkaac
}

public sealed class AacSettings : AudioEncoderSettings
{
    public AacSettings()
    {
        BitrateMode = AudioBitrateMode.Vbr;
        Bitrate = 192;
    }

    public AacEncoder Encoder { get; set; } = AacEncoder.FfmpegAac;
    public string? Profile { get; set; }
}
