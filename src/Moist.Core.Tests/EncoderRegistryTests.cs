using Moist.Core.Encoders;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

public class EncoderRegistryTests
{
    [Fact]
    public void Register_AddsEncoder()
    {
        var registry = new EncoderRegistry();
        var info = new EncoderInfo
        {
            Id = "test-encoder",
            Name = "Test Encoder",
            Category = "Video",
            SettingsType = typeof(VideoEncoderSettings),
        };

        registry.Register(info, () => new FakeProcessor());

        Assert.Contains("test-encoder", registry.Encoders.Keys);
    }

    [Fact]
    public void CreateProcessor_ReturnsProcessor()
    {
        var registry = new EncoderRegistry();
        registry.Register(new EncoderInfo
        {
            Id = "test",
            Name = "Test",
            Category = "Video",
            SettingsType = typeof(VideoEncoderSettings),
        }, () => new FakeProcessor());

        var processor = registry.CreateProcessor("test");
        Assert.NotNull(processor);
        Assert.Equal("fake", processor.Id);
    }

    [Fact]
    public void CreateProcessor_UnknownId_Throws()
    {
        var registry = new EncoderRegistry();
        Assert.Throws<InvalidOperationException>(() => registry.CreateProcessor("nonexistent"));
    }

    [Fact]
    public void GetVideoEncoders_FiltersCorrectly()
    {
        var registry = new EncoderRegistry();
        registry.Register(new EncoderInfo { Id = "v1", Name = "V1", Category = "Video", SettingsType = typeof(VideoEncoderSettings) },
            () => new FakeProcessor());
        registry.Register(new EncoderInfo { Id = "a1", Name = "A1", Category = "Audio", SettingsType = typeof(object) },
            () => new FakeProcessor());

        var video = registry.GetVideoEncoders().ToList();
        var audio = registry.GetAudioEncoders().ToList();

        Assert.Single(video);
        Assert.Single(audio);
        Assert.Equal("v1", video[0].Id);
        Assert.Equal("a1", audio[0].Id);
    }

    private class FakeProcessor : IJobProcessor
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool CanProcess(Job job) => false;
        public Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct)
            => Task.CompletedTask;
    }
}
