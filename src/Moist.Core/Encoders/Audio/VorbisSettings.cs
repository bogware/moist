namespace Moist.Core.Encoders.Audio;

/// <summary>
/// Ogg Vorbis encoder settings via FFmpeg's libvorbis.
/// </summary>
public sealed class VorbisSettings : AudioEncoderSettings
{
    public VorbisSettings()
    {
        BitrateMode = AudioBitrateMode.Vbr;
        Bitrate = 160;
    }

    /// <summary>VBR quality scale: -1 (lowest ~45 kbps) to 10 (highest ~500 kbps). Default 3 ~112 kbps.</summary>
    public int QualityScale { get; set; } = 3;
}
