namespace Moist.Core.Jobs;

/// <summary>
/// Processes a job by launching an external tool and monitoring its progress.
/// Inspired by MeGUI's IJobProcessor but modernized with async/CancellationToken.
/// </summary>
public interface IJobProcessor
{
    string Id { get; }
    string DisplayName { get; }
    bool CanProcess(Job job);
    Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct);
}
