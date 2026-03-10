using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Standalone x265 CLI encoder. Pipes raw video from FFmpeg.
/// </summary>
public sealed partial class X265CliProcessor : CommandLineProcessor
{
    private readonly string _x265Path;
    private readonly string _ffmpegPath;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => "cli-x265";
    public override string DisplayName => "x265 (CLI)";

    public X265CliProcessor(string x265Path, string ffmpegPath)
    {
        _x265Path = x265Path;
        _ffmpegPath = ffmpegPath;
    }

    public override bool CanProcess(Job job)
        => job is VideoJob vj && vj.EncoderId == Id;

    protected override string GetExecutablePath(Job job) => _x265Path;

    protected override string BuildArguments(Job job)
    {
        // Not used directly — OnBeforeStart builds the pipe command
        return string.Empty;
    }

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        _stopwatch.Reset();
        var vj = (VideoJob)job;
        var s = vj.Settings;
        var x265Args = new List<string>();

        switch (s.Mode)
        {
            case VideoEncodingMode.ConstantRateFactor:
                x265Args.AddRange(["--crf", s.Quality.ToString("F1", CultureInfo.InvariantCulture)]);
                break;
            case VideoEncodingMode.SinglePassBitrate:
                x265Args.AddRange(["--bitrate", s.Bitrate.ToString()]);
                break;
            case VideoEncodingMode.TwoPassBitrate:
                x265Args.AddRange(["--bitrate", s.Bitrate.ToString()]);
                break;
        }

        x265Args.AddRange(["--preset", s.Preset.ToString().ToLowerInvariant()]);

        if (s.Tune != VideoTune.None)
            x265Args.AddRange(["--tune", s.Tune.ToString().ToLowerInvariant()]);

        if (s is X265Settings x265)
        {
            x265Args.AddRange(["--profile", x265.Profile]);
            if (x265.Level is not null)
                x265Args.AddRange(["--level-idc", x265.Level]);
            if (x265.Hdr)
                x265Args.Add("--hdr10");
            if (x265.MasterDisplay is not null)
                x265Args.AddRange(["--master-display", x265.MasterDisplay]);
            if (x265.MaxCll is not null)
                x265Args.AddRange(["--max-cll", x265.MaxCll]);
        }

        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            x265Args.Add(s.CustomArgs);

        x265Args.AddRange(["-o", ShellEscape(vj.OutputFile), "--y4m", "-"]);

        var filterArgs = !string.IsNullOrWhiteSpace(vj.FilterScript)
            ? $"-filter_complex {ShellEscape(vj.FilterScript)}"
            : "";

        var x265ArgsStr = string.Join(" ", x265Args);

        // Sanitize paths to prevent shell injection
        var safeInput = ShellEscape(vj.InputFile);
        var safeFfmpeg = ShellEscape(_ffmpegPath);
        var safeX265 = ShellEscape(_x265Path);
        var cmd = $"{safeFfmpeg} -y -hide_banner -i {safeInput} {filterArgs} -f yuv4mpegpipe -strict -1 - | {safeX265} {x265ArgsStr}";

        psi.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        psi.Arguments = $"{(OperatingSystem.IsWindows() ? "/c" : "-c")} {(OperatingSystem.IsWindows() ? cmd : $"\"{cmd}\"")}";
    }

    /// <summary>Escape a value for safe shell interpolation.</summary>
    private static string ShellEscape(string s)
    {
        if (OperatingSystem.IsWindows())
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        // x265 progress: 1234 frames: 45.67 fps, 1234.56 kb/s
        var match = ProgressRegex().Match(line);
        if (match.Success)
        {
            long.TryParse(match.Groups[1].Value, out var frames);
            double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var fps);

            return new JobProgress
            {
                FramesProcessed = frames,
                Fps = fps,
                Elapsed = _stopwatch.Elapsed,
                StatusLine = line.Trim()
            };
        }

        return null;
    }

    [GeneratedRegex(@"(\d+)\s+frames:\s+([\d.]+)\s+fps")]
    private static partial Regex ProgressRegex();
}
