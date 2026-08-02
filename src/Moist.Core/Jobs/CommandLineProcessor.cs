using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Jobs;

/// <summary>
/// Base class for job processors that execute external CLI tools.
/// Inspired by MeGUI's CommandlineJobProcessor but async-first.
/// </summary>
public abstract class CommandLineProcessor : IJobProcessor
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract bool CanProcess(Job job);

    /// <summary>Logger instance. Set via <see cref="SetLogger"/> or defaults to NullLogger.</summary>
    protected ILogger Logger { get; private set; } = NullLogger.Instance;

    public void SetLogger(ILoggerFactory? factory)
    {
        if (factory is not null)
            Logger = factory.CreateLogger(GetType());
    }

    protected abstract string GetExecutablePath(Job job);
    protected abstract string BuildArguments(Job job);
    protected abstract JobProgress? ParseOutputLine(string line, Job job);

    public async Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var exe = GetExecutablePath(job);

        // Validate executable exists before building args or starting process
        if (!IsExecutableAccessible(exe))
        {
            var msg = $"{DisplayName}: executable '{exe}' not found. " +
                      "Check Settings > Tool Paths or install the required tool.";
            Logger.LogError("{Msg}", msg);
            throw new FileNotFoundException(msg, exe);
        }

        var args = BuildArguments(job);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        OnBeforeStart(psi, job);

        // Logged after OnBeforeStart, because processors that pipe two tools together
        // replace the command entirely there. Logging the pre-override version meant the
        // recorded command line was one that never ran, which is exactly the thing you
        // reach for when a job fails.
        var command = DescribeCommand(psi);
        Logger.LogInformation("Starting {Processor} for job {JobId}: {Command}",
            DisplayName, job.Id, command);

        // Log the command line to the job's output log for UI visibility
        job.AppendLog($"[{DisplayName}] {command}");

        using var process = new Process { StartInfo = psi };
        var outputLines = new ConcurrentQueue<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            outputLines.Enqueue(e.Data);
            job.AppendLog(e.Data);
            var p = ParseOutputLine(e.Data, job);
            if (p is not null) progress.Report(p);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            outputLines.Enqueue(e.Data);
            job.AppendLog(e.Data);
            var p = ParseOutputLine(e.Data, job);
            if (p is not null) progress.Report(p);
        };

        if (!process.Start())
        {
            Logger.LogError("Failed to start process: {Exe}", exe);
            throw new InvalidOperationException(
                $"Failed to start {DisplayName}. Ensure '{exe}' is installed and accessible.");
        }

        Logger.LogDebug("{Processor} process started (PID {Pid})", DisplayName, process.Id);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Logger.LogWarning("{Processor} cancelled for job {JobId}, killing process", DisplayName, job.Id);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        if (!IsSuccessExitCode(process.ExitCode))
        {
            var lastLines = string.Join("\n", outputLines.Reverse().Take(10).Reverse());
            Logger.LogError("{Processor} failed with exit code {ExitCode} for job {JobId}:\n{Output}",
                DisplayName, process.ExitCode, job.Id, lastLines);
            throw new InvalidOperationException(
                $"{DisplayName} exited with code {process.ExitCode}.\n{lastLines}");
        }

        await OnAfterSuccessAsync(job, ct);

        Logger.LogInformation("{Processor} completed successfully for job {JobId}", DisplayName, job.Id);
    }

    protected virtual void OnBeforeStart(ProcessStartInfo psi, Job job) { }

    /// <summary>
    /// Runs after the tool exits successfully. Throwing here fails the job, which is the
    /// point: a zero exit code doesn't always mean the output is what was asked for.
    /// </summary>
    protected virtual Task OnAfterSuccessAsync(Job job, CancellationToken ct) => Task.CompletedTask;
    protected virtual bool IsSuccessExitCode(int code) => code == 0;

    /// <summary>Wraps a string in double quotes for CLI argument use.</summary>
    protected static string Quote(string s) => $"\"{s}\"";

    /// <summary>The command as it will actually be run, for logs.</summary>
    private static string DescribeCommand(ProcessStartInfo psi)
    {
        var args = psi.ArgumentList.Count > 0
            ? string.Join(" ", psi.ArgumentList)
            : psi.Arguments;
        return $"{psi.FileName} {args}";
    }

    /// <summary>Check if an executable is accessible (absolute path exists or found on PATH).</summary>
    private static bool IsExecutableAccessible(string exe)
    {
        // If it's an absolute or relative path, check file existence
        if (exe.Contains(Path.DirectorySeparatorChar) || exe.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(exe);

        // Otherwise it's a bare name — check if it's on PATH by trying to find it
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir, exe + ext);
                if (File.Exists(fullPath))
                    return true;
            }
        }

        return false;
    }
}
