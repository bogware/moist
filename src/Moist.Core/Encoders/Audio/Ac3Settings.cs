namespace Moist.Core.Encoders.Audio;

/// <summary>
/// AC3 (Dolby Digital) encoder settings via FFmpeg's ac3 encoder.
/// </summary>
public sealed class Ac3Settings : AudioEncoderSettings
{
    public Ac3Settings()
    {
        BitrateMode = AudioBitrateMode.Cbr;
        Bitrate = 384;
        ChannelMode = AudioChannelMode.KeepOriginal;
    }

    /// <summary>Dialogue normalization level in dB (-31 to -1, default -31 = disabled).</summary>
    public int DialogueNormalization { get; set; } = -31;

    /// <summary>Surround downmix mode (applied during encoding).</summary>
    public Ac3SurroundMode SurroundMode { get; set; } = Ac3SurroundMode.NotIndicated;
}

public enum Ac3SurroundMode
{
    NotIndicated,
    On,
    Off
}
