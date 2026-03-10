using Moist.Core.Muxers;

namespace Moist.Core.Jobs;

public sealed class MuxJob : Job
{
    public override string JobType => "Mux";
    public string MuxerId { get; set; } = string.Empty;
    public ContainerFormat Container { get; set; } = ContainerFormat.Mkv;
    public string? VideoInput { get; set; }
    public List<AudioTrackInput> AudioInputs { get; set; } = [];
    public List<SubtitleTrackInput> SubtitleInputs { get; set; } = [];
    public string? ChapterFile { get; set; }
    public List<string> Attachments { get; set; } = [];
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
