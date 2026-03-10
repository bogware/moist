using Moist.Core.Jobs;
using Moist.Core.Muxers;

namespace Moist.Core.Tests;

public class JobModelTests
{
    [Fact]
    public void VideoJob_HasUniqueId()
    {
        var job1 = new VideoJob();
        var job2 = new VideoJob();

        Assert.NotEqual(job1.Id, job2.Id);
    }

    [Fact]
    public void VideoJob_DefaultStatus_IsPending()
    {
        var job = new VideoJob();
        Assert.Equal(JobStatus.Pending, job.Status);
    }

    [Fact]
    public void MuxJob_FilesToCleanup_IsEmpty()
    {
        var muxJob = new MuxJob();
        Assert.Empty(muxJob.FilesToCleanup);
    }

    [Fact]
    public void AudioJob_AudioStreamIndex_DefaultsToZero()
    {
        var job = new AudioJob();
        Assert.Equal(0, job.AudioStreamIndex);
    }

    [Fact]
    public void ContainerFormat_GetExtension_ReturnsCorrectValues()
    {
        Assert.Equal(".mkv", ContainerFormat.Mkv.GetExtension());
        Assert.Equal(".mp4", ContainerFormat.Mp4.GetExtension());
        Assert.Equal(".webm", ContainerFormat.Webm.GetExtension());
    }

    [Fact]
    public void ContainerFormat_GetFfmpegFormat_ReturnsCorrectValues()
    {
        Assert.Equal("matroska", ContainerFormat.Mkv.GetFfmpegFormat());
        Assert.Equal("mp4", ContainerFormat.Mp4.GetFfmpegFormat());
        Assert.Equal("webm", ContainerFormat.Webm.GetFfmpegFormat());
    }
}
