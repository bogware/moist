using Moist.Core.Muxers;

namespace Moist.Core.Jobs;

public sealed class MuxJob : Job
{
    public override string JobType => "Mux";
    public string MuxerId { get; set; } = string.Empty;
    public ContainerFormat Container { get; set; } = ContainerFormat.Mkv;
    public string? VideoInput { get; set; }

    /// <summary>
    /// Canonical FFmpeg stream codec of <see cref="VideoInput"/> (h264, hevc, av1, …).
    /// Lets the muxer apply a bitstream filter when the target container needs one.
    /// Null when unknown — no filter is applied.
    /// </summary>
    public string? VideoCodec { get; set; }

    public List<AudioTrackInput> AudioInputs { get; set; } = [];
    public List<SubtitleTrackInput> SubtitleInputs { get; set; } = [];
    public string? ChapterFile { get; set; }

    /// <summary>
    /// Original source file, used to recover things that only exist in the container and
    /// therefore never reach our intermediate files: chapters, attachments, global tags.
    /// </summary>
    public string? SourceFile { get; set; }

    /// <summary>Copy chapter markers from <see cref="SourceFile"/>.</summary>
    public bool CopyChapters { get; set; } = true;

    /// <summary>
    /// Copy attachments (typically the fonts styled subtitles reference) from
    /// <see cref="SourceFile"/>. Matroska only — other containers have no equivalent.
    /// </summary>
    public bool CopyAttachments { get; set; } = true;

    /// <summary>Copy container-level tags (title, encoder notes, …) from <see cref="SourceFile"/>.</summary>
    public bool CopyGlobalMetadata { get; set; } = true;

    /// <summary>Total duration in seconds, used for mux progress percentage.</summary>
    public double TotalDurationSeconds { get; set; }

    /// <summary>
    /// EXPERIMENTAL, opt-in: before muxing, attempt to carry the source's Dolby Vision RPU /
    /// HDR10+ dynamic metadata into the encoded <see cref="VideoInput"/> via dovi_tool /
    /// hdr10plus_tool. Guarded and best-effort — it never fails the mux. See DynamicHdrPreserver.
    /// </summary>
    public bool PreserveDynamicHdr { get; set; }

    /// <summary>Source carries a Dolby Vision RPU (set by the builder from probe side-data).</summary>
    public bool SourceHasDolbyVision { get; set; }

    /// <summary>Source carries HDR10+ dynamic metadata (set by the builder from probe side-data).</summary>
    public bool SourceHasHdr10Plus { get; set; }
}

public sealed class AudioTrackInput
{
    public string FilePath { get; set; } = string.Empty;
    public string? Language { get; set; }
    public string? TrackName { get; set; }
    public int DelayMs { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class SubtitleTrackInput
{
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Subtitle-relative index of the stream to take from <see cref="FilePath"/> (the Nth
    /// subtitle stream, not the Nth overall). Null means the file has a single subtitle
    /// stream to map wholesale (an external .srt/.ass). Set for source subtitles so a subset
    /// selection maps only the chosen streams instead of every subtitle in the file.
    /// </summary>
    public int? SourceStreamIndex { get; set; }

    public string? Language { get; set; }
    public string? TrackName { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}
