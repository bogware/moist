using Moist.Core.Jobs;
using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>
/// Jobs must never carry relative paths: GPU super-resolution runs FFmpeg with its working
/// directory set to the shader folder, so `--temp-dir tmp` pointed the intermediate into the
/// shader folder and the encode failed with "Error opening output … No such file or directory".
/// </summary>
public class AbsolutePathTests : IDisposable
{
    private readonly string _rel = "moist_relpath_" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        try { Directory.Delete(_rel, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Build_MakesEveryJobPathAbsolute()
    {
        var req = new EncodeRequest
        {
            InputFile = Path.Combine(_rel, "in.mkv"),
            OutputFile = Path.Combine(_rel, "out", "result.mkv"),
            TempDirectory = Path.Combine(_rel, "tmp"),
            ExternalSubtitleFile = Path.Combine(_rel, "subs.srt"),
        };

        var plan = EncodeJobBuilder.Build(req, info: null);

        foreach (var job in plan.AllJobs)
        {
            Assert.True(Path.IsPathRooted(job.InputFile), $"{job.Name}: input {job.InputFile}");
            Assert.True(Path.IsPathRooted(job.OutputFile), $"{job.Name}: output {job.OutputFile}");
        }
        Assert.All(plan.Mux!.FilesToCleanup, f => Assert.True(Path.IsPathRooted(f), f));
        Assert.Equal(Path.GetFullPath(Path.Combine(_rel, "out", "result.mkv")), plan.Mux.OutputFile);
    }

    [Fact]
    public void NullDevices_AreLeftAlone()
    {
        var req = new EncodeRequest { InputFile = "a.mkv", OutputFile = "NUL", TempDirectory = "t" }.WithAbsolutePaths();
        Assert.Equal("NUL", req.OutputFile);
        Assert.True(Path.IsPathRooted(req.InputFile));
    }
}
