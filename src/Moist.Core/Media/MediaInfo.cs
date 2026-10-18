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

    /// <summary>
    /// Timestamp of the first video frame. In a broadcast capture or a stream-copied cut this
    /// is typically ~1 s after the first audio packet (the recording starts mid-GOP), and the
    /// builder uses the difference to keep audio and video in sync.
    /// </summary>
    public TimeSpan StartTime { get; init; }

    /// <summary>
    /// True only when <see cref="TotalFrames"/> is a real container measurement (nb_frames),
    /// false when it is the duration*r_frame_rate estimate. The estimate overshoots badly for
    /// variable-frame-rate content, so callers checking for truncation must trust it only when
    /// this is set.
    /// </summary>
    public bool FrameCountIsExact { get; init; }

    public int BitDepth { get; init; } = 8;
    public string PixelFormat { get; init; } = string.Empty;
    public string? DisplayAspectRatio { get; init; }
    public long BitrateKbps { get; init; }

    // ---- Colour and HDR ----

    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }

    /// <summary>Static HDR10 metadata (mastering display / content light level) is present.</summary>
    public bool HasHdr10Metadata { get; init; }

    /// <summary>HDR10+ dynamic metadata is present. Lost on re-encode without hdr10plus_tool.</summary>
    public bool HasHdr10Plus { get; init; }

    /// <summary>A Dolby Vision RPU is present. Lost on re-encode without dovi_tool.</summary>
    public bool HasDolbyVision { get; init; }

    /// <summary>
    /// The source carries an HDR transfer function. PQ (smpte2084) and HLG are the two
    /// that matter; anything else is SDR regardless of what else is tagged.
    /// </summary>
    public bool IsHdr =>
        ColorTransfer is "smpte2084" or "arib-std-b67"
        || HasHdr10Metadata || HasHdr10Plus || HasDolbyVision;

    /// <summary>Dynamic metadata needs external tooling to survive a re-encode.</summary>
    public bool HasDynamicHdrMetadata => HasHdr10Plus || HasDolbyVision;
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

    /// <summary>Timestamp of this stream's first packet; compared with the video's to keep sync.</summary>
    public TimeSpan StartTime { get; init; }
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
