namespace Moist.Core.Jobs;

/// <summary>
/// Base class for all encoding jobs. Inspired by MeGUI's Job class
/// but modernized with async support and strong typing.
/// </summary>
public abstract class Job
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string InputFile { get; set; } = string.Empty;
    public string OutputFile { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<string> FilesToCleanup { get; set; } = [];

    public abstract string JobType { get; }
}
