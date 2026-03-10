namespace Moist.Core.Encoders.Audio;

public sealed class OpusSettings : AudioEncoderSettings
{
    public OpusSettings()
    {
        BitrateMode = AudioBitrateMode.Vbr;
        Bitrate = 128;
    }

    public int Complexity { get; set; } = 10;
}
