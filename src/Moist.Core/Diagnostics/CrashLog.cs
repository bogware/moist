using Moist.Core.Settings;
namespace Moist.Core.Diagnostics;

/// <summary>
/// Last-resort crash capture, independent of the logging pipeline so it works even when that
/// has failed or isn't built yet. Writes one file per incident to
/// <c>%AppData%/moist/logs/crash_&lt;timestamp&gt;.log</c>, so a crash anywhere — UI thread,
/// worker, or a fire-and-forget task — leaves evidence instead of vanishing with the window.
/// Everything here is best-effort and must never throw.
/// </summary>
public static class CrashLog
{
    private static int _installed;

    public static string LogDirectory => Path.Combine(MoistPaths.ConfigRoot, "logs");

    /// <summary>
    /// Hook the process-wide escape hatches. Idempotent. <paramref name="host"/> names the
    /// entry point ("gui" / "cli") so the crash file says which binary died.
    /// </summary>
    public static void InstallGlobalHandlers(string host)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(host, "AppDomain.UnhandledException" + (e.IsTerminating ? " (terminating)" : ""),
                e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(host, "TaskScheduler.UnobservedTaskException", e.Exception);
            // Recorded — don't let a faulted fire-and-forget task escalate any further.
            e.SetObserved();
        };
    }

    /// <summary>Write an incident file. Returns its path, or null if even that failed.</summary>
    public static string? Write(string host, string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"crash_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
            var text =
                $"moist {host} — {source}{Environment.NewLine}" +
                $"{DateTime.Now:O}{Environment.NewLine}" +
                $"Process {Environment.ProcessId} · {Environment.OSVersion} · .NET {Environment.Version}{Environment.NewLine}" +
                $"{Environment.NewLine}{exception}{Environment.NewLine}";
            File.WriteAllText(path, text);
            return path;
        }
        catch
        {
            return null; // a crash logger that crashes helps nobody
        }
    }
}
