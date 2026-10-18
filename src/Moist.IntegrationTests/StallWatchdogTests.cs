using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.IntegrationTests;

/// <summary>
/// A tool that goes silent must be killed and reported as stalled — never waited on forever.
/// Uses ffmpeg generating 30s of nothing with all output suppressed: a perfect "hung" tool.
/// </summary>
public class StallWatchdogTests
{
    private sealed class SilentProcessor : CommandLineProcessor
    {
        public override string Id => "test-silent";
        public override string DisplayName => "Silent Tool";
        public override bool CanProcess(Job job) => true;
        protected override TimeSpan StallTimeout => TimeSpan.FromSeconds(3);
        protected override string GetExecutablePath(Job job) => "ffmpeg";
        protected override string BuildArguments(Job job) =>
            "-re -loglevel quiet -nostats -f lavfi -i nullsrc=s=16x16:r=1:d=30 -f null -";
        protected override JobProgress? ParseOutputLine(string line, Job job) => null;
    }

    [FfmpegFact]
    public async Task SilentToolIsKilledAndReportedAsStalled()
    {
        var job = new VideoJob
        {
            Name = "stall", InputFile = "unused", OutputFile = "unused", EncoderId = "test-silent",
            Settings = new VideoEncoderSettings(),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            new SilentProcessor().ProcessAsync(job, new Progress<JobProgress>(), CancellationToken.None));

        Assert.Contains("stalled", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"took {sw.Elapsed:g}; the 30s silent tool was not killed promptly");
    }

    /// <summary>The frame-based AI processors (Real-ESRGAN, RIFE) don't go through
    /// <see cref="CommandLineProcessor"/>; they call the shared watchdog directly. Exercise that
    /// entry point against the same silent tool so both paths are proven to kill a hang.</summary>
    [FfmpegFact]
    public async Task ProcessWatchdog_KillsASilentProcessDirectly()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-re", "-loglevel", "quiet", "-nostats", "-f", "lavfi", "-i", "nullsrc=s=16x16:r=1:d=30", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var started = DateTime.UtcNow.Ticks;   // never refreshed: the tool is "silent" from the first tick
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => ProcessWatchdog.WaitForExitOrStallAsync(
            p, () => started, TimeSpan.FromSeconds(3), "Silent AI Tool", NullLogger.Instance, CancellationToken.None));

        Assert.Contains("Silent AI Tool", ex.Message);
        Assert.Contains("stalled", ex.Message);
        Assert.True(p.HasExited, "the stalled process was not killed");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed:g}");
    }
}
