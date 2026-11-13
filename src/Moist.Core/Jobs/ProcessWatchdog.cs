using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Moist.Core.Jobs;

/// <summary>
/// Waits on an external tool, but refuses to wait forever: a process that produces no output on
/// either pipe for <c>stallTimeout</c> is killed and reported as stalled.
///
/// Every tool moist runs reports continuously while it works (ffmpeg's <c>-progress</c> or stats
/// line, x264/x265's status line, Real-ESRGAN's and RIFE's per-frame percentages), so prolonged
/// silence means a hang — a stuck read from a network share, a deadlocked filter, a wedged GPU
/// driver — not slow work. Without this, an unattended batch sits on a hung job until someone
/// notices. Shared by <see cref="CommandLineProcessor"/> and the frame-based AI processors so
/// they all fail the same way, with the same message.
/// </summary>
internal static class ProcessWatchdog
{
    /// <summary>The default silence budget. Generous on purpose: a false positive kills real work.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wait for <paramref name="process"/> to exit. If <paramref name="stallTimeout"/> passes with
    /// <paramref name="lastOutputTicks"/> unchanged, the process tree is killed and a
    /// <see cref="TimeoutException"/> is thrown. Cancellation behaves exactly as a plain wait would.
    /// </summary>
    /// <param name="lastOutputTicks">Returns the UTC tick count of the most recent output line; the
    /// caller refreshes it from its <c>DataReceived</c> handlers (use <see cref="Volatile"/>).</param>
    public static async Task WaitForExitOrStallAsync(
        Process process, Func<long> lastOutputTicks, TimeSpan stallTimeout,
        string displayName, ILogger logger, CancellationToken ct)
    {
        var exit = process.WaitForExitAsync(ct);
        var poll = TimeSpan.FromSeconds(Math.Clamp(stallTimeout.TotalSeconds / 10, 1, 30));
        while (true)
        {
            var done = await Task.WhenAny(exit, Task.Delay(poll, ct));
            if (done == exit) { await exit; return; }      // normal exit, or propagates cancellation
            ct.ThrowIfCancellationRequested();

            var silent = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastOutputTicks());
            if (silent < stallTimeout) continue;

            logger.LogError("{Processor} produced no output for {Silent:g}; treating it as stalled and killing it",
                displayName, silent);
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                // Kill only *signals* on Unix (SIGKILL) and returns before the process is reaped.
                // Wait briefly so the caller's cleanup (scratch deletion, retry) never races a
                // tool that is still alive and still holding its files.
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch { /* best effort */ }
            throw new TimeoutException(
                $"{displayName} produced no output for {silent:hh\\:mm\\:ss} and was killed as stalled. " +
                "This usually means a hung read (network share?), a stuck filter, or a wedged GPU driver.");
        }
    }
}
