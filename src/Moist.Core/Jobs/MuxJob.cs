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
    public List<string> Attachments { get; set; } = [];

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
    public string? Language { get; set; }
    public string? TrackName { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}
