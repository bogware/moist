namespace Moist.Core.Media;

public sealed class MediaFileInfo
{
    public string FilePath { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public long FileSizeBytes { get; init; }

    public VideoStreamInfo? Video { get; init; }
    public List<AudioStreamInfo> AudioStreams { get; init; } = [];
    public List<SubtitleStreamInfo> SubtitleStreams { get; init; } = [];
}

public sealed class VideoStreamInfo
{
    public string Codec { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public double Framerate { get; init; }
    public long TotalFrames { get; init; }
    public int BitDepth { get; init; } = 8;
    public string PixelFormat { get; init; } = string.Empty;
    public string? DisplayAspectRatio { get; init; }
    public long BitrateKbps { get; init; }
}

public sealed class AudioStreamInfo
{
    public int Index { get; init; }
    public string Codec { get; init; } = string.Empty;
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public long BitrateKbps { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
}

public sealed class SubtitleStreamInfo
{
    public int Index { get; init; }
    public string Codec { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
}
