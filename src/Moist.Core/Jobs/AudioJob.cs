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

    /// <summary>
    /// Drop this much from the start of the source audio before encoding. Set by the builder
    /// when the audio stream begins before the first video frame (the norm for broadcast
    /// captures and stream-copied cuts): the video encode starts at that first frame, so audio
    /// that led it must lose exactly that lead or the result plays out of lip-sync.
    /// </summary>
    public TimeSpan? TrimStart { get; set; }
    public string? Language { get; set; }
    public string? TrackName { get; set; }

    /// <summary>Total duration of the source in seconds, used for progress calculation.</summary>
    public double TotalDurationSeconds { get; set; }
}
