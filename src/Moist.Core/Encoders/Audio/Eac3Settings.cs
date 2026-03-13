namespace Moist.Core.Encoders.Audio;

/// <summary>
/// E-AC3 (Dolby Digital Plus / Enhanced AC-3) encoder settings.
/// </summary>
public sealed class Eac3Settings : AudioEncoderSettings
{
    public Eac3Settings()
    {
        BitrateMode = AudioBitrateMode.Cbr;
        Bitrate = 448;
        ChannelMode = AudioChannelMode.KeepOriginal;
    }

    /// <summary>Dialogue normalization level in dB (-31 to -1, default -31 = disabled).</summary>
    public int DialogueNormalization { get; set; } = -31;

    /// <summary>Mixing level (dB, 80-111, controls the peak mixing level).</summary>
    public int? MixingLevel { get; set; }
}
