using Moist.Core.Jobs;

namespace Moist.Core.Tests;

public class JobQueueTests : IDisposable
{
    private readonly JobQueue _queue = new(maxConcurrentJobs: 2);

    public void Dispose() => _queue.Dispose();

    [Fact]
    public void Enqueue_AddsJobToQueue()
    {
        var job = new VideoJob { Name = "Test" };
        _queue.Enqueue(job);

        Assert.Single(_queue.Jobs);
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public void Enqueue_SetsStatusToQueued()
    {
        var job = new AudioJob { Name = "Audio test" };
        _queue.Enqueue(job);

        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public void Remove_RemovesJobFromQueue()
    {
        var job = new VideoJob { Name = "Test" };
        _queue.Enqueue(job);
        _queue.Remove(job.Id);

        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void CancelJob_SetsCancelledStatus()
    {
        var job = new VideoJob { Name = "Test" };
        _queue.Enqueue(job);
        _queue.CancelJob(job.Id);

        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void CancelJob_NonExistentId_DoesNotThrow()
    {
        _queue.CancelJob("non-existent-id");
    }

    [Fact]
    public void JobDependencies_AreTracked()
    {
        var job1 = new VideoJob { Name = "Video" };
        var job2 = new AudioJob { Name = "Audio" };
        var muxJob = new MuxJob
        {
            Name = "Mux",
            DependsOn = [job1.Id, job2.Id]
        };

        _queue.Enqueue(job1);
        _queue.Enqueue(job2);
        _queue.Enqueue(muxJob);

        Assert.Equal(3, _queue.Jobs.Count());
        Assert.Contains(job1.Id, muxJob.DependsOn);
        Assert.Contains(job2.Id, muxJob.DependsOn);
    }

    [Fact]
    public void StartAsync_WhenAlreadyRunning_DoesNotStartAgain()
    {
        // Should not throw when called twice
        _ = _queue.StartAsync();
        _ = _queue.StartAsync();

        Assert.True(_queue.IsRunning);
    }

    [Fact]
    public void Stop_WhenRunning_StopsQueue()
    {
        _ = _queue.StartAsync();
        _queue.Stop();

        Assert.False(_queue.IsRunning);
    }
}
