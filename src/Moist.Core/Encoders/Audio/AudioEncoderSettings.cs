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

public enum AudioNormalizationMode
{
    None,
    EbuR128,
    Peak,
    Rms
}

public enum AudioSampleFormat
{
    Auto,
    S16,
    S24,
    S32,
    Float
}

public class AudioEncoderSettings
{
    public AudioBitrateMode BitrateMode { get; set; } = AudioBitrateMode.Vbr;
    public int Bitrate { get; set; } = 128;
    public double VbrQuality { get; set; } = 5.0;
    public int? SampleRate { get; set; }
    public AudioChannelMode ChannelMode { get; set; } = AudioChannelMode.KeepOriginal;
    public string CustomArgs { get; set; } = string.Empty;

    /// <summary>Audio normalization mode.</summary>
    public AudioNormalizationMode Normalization { get; set; } = AudioNormalizationMode.None;

    /// <summary>Target loudness in LUFS for EBU R128 normalization (typically -14 to -24).</summary>
    public double NormalizationTarget { get; set; } = -16.0;

    /// <summary>True peak limit in dBTP for normalization.</summary>
    public double TruePeakLimit { get; set; } = -1.0;

    /// <summary>Sample format for output.</summary>
    public AudioSampleFormat SampleFormat { get; set; } = AudioSampleFormat.Auto;

    /// <summary>Bit depth for output (16, 24, 32). Null = keep original.</summary>
    public int? BitDepth { get; set; }
}
