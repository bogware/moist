namespace Moist.Core.Jobs;

public sealed class JobProgress
{
    public double Percentage { get; init; }
    public long FramesProcessed { get; init; }
    public long TotalFrames { get; init; }
    public double Fps { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan Eta { get; init; }
    public long OutputSizeBytes { get; init; }
    public string? StatusLine { get; init; }
}
