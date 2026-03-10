namespace Moist.Core.Jobs;

public enum JobStatus
{
    Pending,
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}
