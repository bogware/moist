namespace Moist.Core.Jobs;

/// <summary>
/// Wraps a video processor to handle two-pass encoding.
/// Pass 1 outputs to /dev/null with -pass 1, pass 2 outputs the real file with -pass 2.
/// Uses snapshot/restore to avoid corrupting job state visible to the UI.
/// </summary>
public sealed class TwoPassProcessor : IJobProcessor
{
    private readonly IJobProcessor _innerProcessor;

    public string Id => _innerProcessor.Id;
    public string DisplayName => $"{_innerProcessor.DisplayName} (2-pass)";

    public TwoPassProcessor(IJobProcessor innerProcessor)
    {
        _innerProcessor = innerProcessor;
    }

    public bool CanProcess(Job job)
    {
        if (job is not VideoJob vj) return false;
        return vj.Settings.Mode == Encoders.Video.VideoEncodingMode.TwoPassBitrate
               && _innerProcessor.CanProcess(job);
    }

    public async Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var vj = (VideoJob)job;

        // Snapshot mutable state before modification
        var originalOutput = vj.OutputFile;
        var originalCustomArgs = vj.Settings.CustomArgs;
        var passLogFile = Path.Combine(
            Path.GetDirectoryName(originalOutput) ?? Path.GetTempPath(),
            $"moist_2pass_{vj.Id}");

        // Detect CLI vs FFmpeg encoder for correct pass syntax
        var isCli = vj.EncoderId.StartsWith("cli-", StringComparison.Ordinal);

        try
        {
            // === Pass 1: Analysis ===
            vj.OutputFile = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            vj.Settings.CustomArgs = BuildPassArgs(originalCustomArgs, passLogFile, pass: 1, isCli);

            var pass1Progress = new Progress<JobProgress>(p =>
            {
                progress.Report(new JobProgress
                {
                    Percentage = p.Percentage * 0.5,
                    FramesProcessed = p.FramesProcessed,
                    TotalFrames = p.TotalFrames,
                    Fps = p.Fps,
                    Elapsed = p.Elapsed,
                    Eta = p.Eta,
                    StatusLine = $"[Pass 1] {p.StatusLine}"
                });
            });

            await _innerProcessor.ProcessAsync(job, pass1Progress, ct);

            // === Pass 2: Encode ===
            vj.OutputFile = originalOutput;
            vj.Settings.CustomArgs = BuildPassArgs(originalCustomArgs, passLogFile, pass: 2, isCli);

            var pass2Progress = new Progress<JobProgress>(p =>
            {
                progress.Report(new JobProgress
                {
                    Percentage = 50 + p.Percentage * 0.5,
                    FramesProcessed = p.FramesProcessed,
                    TotalFrames = p.TotalFrames,
                    Fps = p.Fps,
                    Elapsed = p.Elapsed,
                    Eta = p.Eta,
                    StatusLine = $"[Pass 2] {p.StatusLine}"
                });
            });

            await _innerProcessor.ProcessAsync(job, pass2Progress, ct);
        }
        finally
        {
            // Always restore original settings even if encoding fails
            vj.OutputFile = originalOutput;
            vj.Settings.CustomArgs = originalCustomArgs;

            // Cleanup pass log files
            CleanupPassLogFiles(passLogFile);
        }
    }

    private static string BuildPassArgs(string originalArgs, string passLogFile, int pass, bool isCli)
    {
        var passArgs = isCli
            ? $"--pass {pass} --stats \"{passLogFile}\""
            : $"-pass {pass} -passlogfile \"{passLogFile}\"";

        return string.IsNullOrWhiteSpace(originalArgs)
            ? passArgs
            : $"{originalArgs} {passArgs}";
    }

    private static void CleanupPassLogFiles(string passLogFile)
    {
        try
        {
            var logDir = Path.GetDirectoryName(passLogFile) ?? ".";
            var logPrefix = Path.GetFileName(passLogFile);
            foreach (var logFile in Directory.GetFiles(logDir, $"{logPrefix}*"))
            {
                try { File.Delete(logFile); } catch { }
            }
        }
        catch { /* best effort cleanup */ }
    }
}
