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
        // FFmpeg's native AAC has no true VBR worth using (its -q:a path is experimental and
        // poor), so moist drives it by target bitrate — which is ABR, i.e. constrained VBR.
        // Label it honestly; the emitted args (-b:a <n>k) are identical to the old "Vbr" default.
        BitrateMode = AudioBitrateMode.ConstrainedVbr;
        Bitrate = 192;
    }

    public AacEncoder Encoder { get; set; } = AacEncoder.FfmpegAac;
    public string? Profile { get; set; }
}
