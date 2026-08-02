using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Standalone x264 CLI encoder. FFmpeg decodes the source into a y4m pipe and x264
/// consumes it, which is the only way to feed x264 anything it cannot demux itself.
/// Supports two-pass encoding natively via --pass/--stats.
/// </summary>
public partial class X264CliProcessor : CommandLineProcessor
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

    /// <summary>
    /// The x264 half of the pipeline. This is what actually runs — <see cref="OnBeforeStart"/>
    /// puts FFmpeg in front of it.
    /// </summary>
    protected override string BuildArguments(Job job)
    {
        var vj = (VideoJob)job;
        _stopwatch.Reset();
        var s = vj.Settings;
        var args = new List<string>();

        switch (s.Mode)
        {
            case VideoEncodingMode.ConstantRateFactor:
                args.AddRange(["--crf", s.Quality.ToString("F1", CultureInfo.InvariantCulture)]);
                break;
            case VideoEncodingMode.SinglePassBitrate:
            case VideoEncodingMode.TwoPassBitrate:
                // The pass number and stats file arrive through CustomArgs, set by TwoPassProcessor.
                args.AddRange(["--bitrate", s.Bitrate.ToString(CultureInfo.InvariantCulture)]);
                break;
        }

        args.AddRange(["--preset", s.Preset.ToString().ToLowerInvariant()]);

        if (s.Tune != VideoTune.None)
            args.AddRange(["--tune", s.Tune.ToString().ToLowerInvariant()]);

        // x264 defaults its output to 8-bit whatever the input depth is, so a 10-bit pipe
        // would otherwise be silently truncated back down. Only sent when it differs from
        // the default, so 8-bit-only builds — which reject the option outright — still work.
        var depth = ShellPipeline.OutputBitDepth(vj);
        if (depth != 8)
            args.AddRange(["--output-depth", depth.ToString(CultureInfo.InvariantCulture)]);

        if (s is X264Settings x264)
        {
            if (x264.Profile is not null)
                args.AddRange(["--profile", x264.Profile]);
            if (x264.Level is not null)
                args.AddRange(["--level", x264.Level]);
            args.AddRange(["--ref", x264.RefFrames.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["--bframes", x264.BFrames.ToString(CultureInfo.InvariantCulture)]);
            if (!x264.Cabac)
                args.Add("--no-cabac");
            if (x264.Threads.HasValue)
                args.AddRange(["--threads", x264.Threads.Value.ToString(CultureInfo.InvariantCulture)]);
            if (x264.BlurayCompat)
                args.Add("--bluray-compat");
        }

        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        args.AddRange(["-o", ShellPipeline.Escape(vj.OutputFile), "--demuxer", "y4m", "-"]);

        return string.Join(" ", args);
    }

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        var vj = (VideoJob)job;
        var decode = ShellPipeline.FfmpegDecodeCommand(_ffmpegPath, vj);
        ShellPipeline.Apply(psi, $"{decode} | {ShellPipeline.Escape(_x264Path)} {BuildArguments(job)}");
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        // Reading a pipe, x264 has no frame count to work from and reports
        // "1234 frames: 45.67 fps, 1234.56 kb/s". The bracketed percentage form only
        // appears when it opened the file itself, which it never does here.
        var match = ProgressRegex().Match(line);
        if (!match.Success) return null;

        long.TryParse(match.Groups[1].Value, out var frames);
        double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var fps);

        var vj = (VideoJob)job;
        var total = vj.TrimFrames ?? vj.TotalFrames;
        var eta = fps > 0 && total > frames
            ? TimeSpan.FromSeconds((total - frames) / fps)
            : TimeSpan.Zero;

        return new JobProgress
        {
            Percentage = ShellPipeline.PercentageOf(frames, vj) ?? 0,
            FramesProcessed = frames,
            TotalFrames = total,
            Fps = fps,
            Elapsed = _stopwatch.Elapsed,
            Eta = eta,
            StatusLine = line.Trim()
        };
    }

    [GeneratedRegex(@"(\d+)\s+frames:\s+([\d.]+)\s+fps")]
    private static partial Regex ProgressRegex();
}
