using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

public class VapourSynthPipeProcessorTests
{
    private static VideoJob Job(string engine = "vapoursynth", string? script = "clip.set_output()") => new()
    {
        InputFile = "in.mkv",
        OutputFile = "out.mkv",
        EncoderId = "ffmpeg-x265",
        Settings = new X265Settings { Mode = VideoEncodingMode.ConstantRateFactor, Quality = 20, Preset = VideoPreset.Slow },
        FilterEngineId = engine,
        FilterScript = script,
        SourceBitDepth = 8,
    };

    [Fact]
    public void CanProcess_OnlyForVapourSynthJobsWithAScript()
    {
        var p = new VapourSynthPipeProcessor("ffmpeg", "vspipe");
        Assert.True(p.CanProcess(Job()));
        Assert.False(p.CanProcess(Job(engine: "ffmpeg")));      // FFmpeg goes to the -vf path
        Assert.False(p.CanProcess(Job(script: null)));          // nothing to run
        Assert.False(p.CanProcess(new AudioJob()));
    }

    [Fact]
    public void BuildConsumerArgs_ReadsY4mFromStdinAndEncodesWithJobSettings()
    {
        var args = VapourSynthPipeProcessor.BuildConsumerArgs(Job());
        Assert.Equal("yuv4mpegpipe", args[args.IndexOf("-f") + 1]);
        Assert.Equal("-", args[args.IndexOf("-i") + 1]);   // stdin
        Assert.Equal("libx265", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("20", args[args.IndexOf("-crf") + 1]);
        Assert.Equal("slow", args[args.IndexOf("-preset") + 1]);
        Assert.Contains("-an", args);                            // video-only; audio is a separate job
    }
}
