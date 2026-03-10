namespace Moist.Core.Encoders.Audio;

public sealed class FlacSettings : AudioEncoderSettings
{
    public FlacSettings()
    {
        BitrateMode = AudioBitrateMode.Vbr; // lossless, bitrate is ignored
    }

    public int CompressionLevel { get; set; } = 5;
}
