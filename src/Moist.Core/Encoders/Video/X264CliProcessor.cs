using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Standalone x264 CLI encoder. Pipes raw video from FFmpeg or reads AVS/VPY directly.
/// Supports two-pass encoding natively.
/// </summary>
public sealed partial class X264CliProcessor : CommandLineProcessor
{
    private readonly string _x264Path;
    private readonly string _ffmpegPath;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => "cli-x264";
    public override string DisplayName => "x264 (CLI)";

    public X264CliProcessor(string x264Path, string ffmpegPath)
    {
        _x264Path = x264Path;
        _ffmpegPath = ffmpegPath;
    }

    public override bool CanProcess(Job job)
        => job is VideoJob vj && vj.EncoderId == Id;

    protected override string GetExecutablePath(Job job) => _x264Path;

    protected override string BuildArguments(Job job)
    {
        var vj = (VideoJob)job;
        _stopwatch.Reset();
        var s = vj.Settings;
        var args = new List<string>();

        // Encoding mode
        switch (s.Mode)
        {
            case VideoEncodingMode.ConstantRateFactor:
                args.AddRange(["--crf", s.Quality.ToString("F1", CultureInfo.InvariantCulture)]);
                break;
            case VideoEncodingMode.SinglePassBitrate:
                args.AddRange(["--bitrate", s.Bitrate.ToString()]);
                break;
            case VideoEncodingMode.TwoPassBitrate:
                args.AddRange(["--bitrate", s.Bitrate.ToString()]);
                // Pass number handled by TwoPassProcessor
                break;
        }

        // Preset
        args.AddRange(["--preset", s.Preset.ToString().ToLowerInvariant()]);

        // Tune
        if (s.Tune != VideoTune.None)
            args.AddRange(["--tune", s.Tune.ToString().ToLowerInvariant()]);

        // x264-specific settings
        if (s is X264Settings x264)
        {
            args.AddRange(["--profile", x264.Profile]);
            if (x264.Level is not null)
                args.AddRange(["--level", x264.Level]);
            args.AddRange(["--ref", x264.RefFrames.ToString()]);
            args.AddRange(["--bframes", x264.BFrames.ToString()]);
            if (!x264.Cabac)
                args.Add("--no-cabac");
            if (x264.Threads.HasValue)
                args.AddRange(["--threads", x264.Threads.Value.ToString()]);
            if (x264.BlurayCompat)
                args.Add("--bluray-compat");
        }

        // Custom args
        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        // Output
        args.AddRange(["-o", Quote(vj.OutputFile)]);

        // Input — use pipe from FFmpeg for format compatibility
        args.Add("--demuxer");
        args.Add("y4m");
        args.Add("-");

        return string.Join(" ", args);
    }

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        var vj = (VideoJob)job;
        var x264Args = BuildX264Args(vj);
        var filterArgs = !string.IsNullOrWhiteSpace(vj.FilterScript)
            ? $"-filter_complex {ShellEscape(vj.FilterScript)}"
            : "";

        // Use shell to handle pipe: ffmpeg | x264
        // Sanitize paths to prevent shell injection
        var safeInput = ShellEscape(vj.InputFile);
        var safeFfmpeg = ShellEscape(_ffmpegPath);
        var safeX264 = ShellEscape(_x264Path);
        var cmd = $"{safeFfmpeg} -y -hide_banner -i {safeInput} {filterArgs} -f yuv4mpegpipe -strict -1 - | {safeX264} {x264Args}";

        psi.FileName = GetShell();
        psi.Arguments = $"{GetShellArg()} {(OperatingSystem.IsWindows() ? cmd : $"\"{cmd}\"")}";
    }

    private string BuildX264Args(VideoJob vj)
    {
        var s = vj.Settings;
        var args = new List<string>();

        switch (s.Mode)
        {
            case VideoEncodingMode.ConstantRateFactor:
                args.AddRange(["--crf", s.Quality.ToString("F1", CultureInfo.InvariantCulture)]);
                break;
            case VideoEncodingMode.SinglePassBitrate:
                args.AddRange(["--bitrate", s.Bitrate.ToString()]);
                break;
            case VideoEncodingMode.TwoPassBitrate:
                args.AddRange(["--bitrate", s.Bitrate.ToString()]);
                break;
        }

        args.AddRange(["--preset", s.Preset.ToString().ToLowerInvariant()]);

        if (s.Tune != VideoTune.None)
            args.AddRange(["--tune", s.Tune.ToString().ToLowerInvariant()]);

        if (s is X264Settings x264)
        {
            args.AddRange(["--profile", x264.Profile]);
            if (x264.Level is not null)
                args.AddRange(["--level", x264.Level]);
        }

        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        args.AddRange(["-o", Quote(vj.OutputFile), "--demuxer", "y4m", "-"]);
        return string.Join(" ", args);
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        // x264 progress: [12.3%] 1234/10000 frames, 45.67 fps, 1234.56 kb/s
        var match = ProgressRegex().Match(line);
        if (match.Success)
        {
            double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var pct);
            long.TryParse(match.Groups[2].Value, out var frames);
            long.TryParse(match.Groups[3].Value, out var total);
            double.TryParse(match.Groups[4].Value, CultureInfo.InvariantCulture, out var fps);

            var eta = fps > 0 && total > 0
                ? TimeSpan.FromSeconds((total - frames) / fps)
                : TimeSpan.Zero;

            return new JobProgress
            {
                Percentage = pct,
                FramesProcessed = frames,
                TotalFrames = total,
                Fps = fps,
                Elapsed = _stopwatch.Elapsed,
                Eta = eta,
                StatusLine = line.Trim()
            };
        }

        return null;
    }

    private static string GetShell() => OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
    private static string GetShellArg() => OperatingSystem.IsWindows() ? "/c" : "-c";

    /// <summary>Escape a value for safe shell interpolation.</summary>
    private static string ShellEscape(string s)
    {
        if (OperatingSystem.IsWindows())
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        // POSIX: single-quote, escaping existing single quotes
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    [GeneratedRegex(@"\[(\d+\.\d+)%\]\s+(\d+)/(\d+)\s+frames,\s+([\d.]+)\s+fps")]
    private static partial Regex ProgressRegex();
}
