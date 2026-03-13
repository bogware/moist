namespace Moist.Core.Encoders.Audio;

/// <summary>
/// LAME MP3 encoder settings via FFmpeg's libmp3lame.
/// </summary>
public sealed class Mp3Settings : AudioEncoderSettings
{
    public Mp3Settings()
    {
        BitrateMode = AudioBitrateMode.Vbr;
        Bitrate = 192;
    }

    /// <summary>VBR quality scale: 0 (best, ~245 kbps) to 9 (worst, ~65 kbps).</summary>
    public int VbrScale { get; set; } = 2;

    /// <summary>Joint stereo mode for better compression at lower bitrates.</summary>
    public Mp3ChannelMode Mp3ChannelMode { get; set; } = Mp3ChannelMode.JointStereo;
}

public enum Mp3ChannelMode
{
    JointStereo,
    Stereo,
    ForcedJointStereo,
    DualMono
}
