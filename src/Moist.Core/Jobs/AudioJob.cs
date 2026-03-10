using Moist.Core.Encoders.Audio;

namespace Moist.Core.Jobs;

public sealed class AudioJob : Job
{
    public override string JobType => "Audio Encode";
    public AudioEncoderSettings Settings { get; set; } = null!;
    public string EncoderId { get; set; } = string.Empty;

    /// <summary>Zero-based audio stream index in the source file to encode.</summary>
    public int AudioStreamIndex { get; set; }
    public int DelayMs { get; set; }
    public string? Language { get; set; }
    public string? TrackName { get; set; }

    /// <summary>Total duration of the source in seconds, used for progress calculation.</summary>
    public double TotalDurationSeconds { get; set; }
}
