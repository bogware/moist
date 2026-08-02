using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Standalone x265 CLI encoder. Pipes raw video from FFmpeg.
/// </summary>
public partial class X265CliProcessor : CommandLineProcessor
{
    private readonly string _x265Path;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => "cli-x265";
    public override string DisplayName => "x265 (CLI)";

    public X265CliProcessor(string x265Path, string ffmpegPath, string ffprobePath = "ffprobe")
    {
        _x265Path = x265Path;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    public override bool CanProcess(Job job)
        => job is VideoJob vj && vj.EncoderId == Id;

    protected override string GetExecutablePath(Job job) => _x265Path;

    /// <summary>
    /// The x265 half of the pipeline. This is what actually runs — <see cref="OnBeforeStart"/>
    /// puts FFmpeg in front of it.
    /// </summary>
    protected override string BuildArguments(Job job)
    {
        _stopwatch.Reset();
        var vj = (VideoJob)job;
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

        // A multilib x265 encodes at its default depth regardless of what the pipe carries,
        // so 10-bit has to be asked for explicitly. Only sent when it differs from 8, so
        // single-depth builds — which reject anything but their own — still work.
        var depth = ShellPipeline.OutputBitDepth(vj);
        if (depth != 8)
            args.AddRange(["--output-depth", depth.ToString(CultureInfo.InvariantCulture)]);

        if (s is X265Settings x265)
        {
            if (x265.Profile is not null)
                args.AddRange(["--profile", x265.Profile]);
            if (x265.Level is not null)
                args.AddRange(["--level-idc", x265.Level]);
            if (x265.Hdr)
                args.Add("--hdr10");
            if (x265.MasterDisplay is not null)
                args.AddRange(["--master-display", x265.MasterDisplay]);
            if (x265.MaxCll is not null)
                args.AddRange(["--max-cll", x265.MaxCll]);
        }

        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        // x265 has no muxer — it writes a raw HEVC elementary stream and nothing else.
        // OnAfterSuccessAsync wraps it afterwards, so the bitstream lands beside the
        // container path the rest of the pipeline is expecting.
        args.AddRange(["-o", ShellPipeline.Escape(RawStreamPath(vj)), "--y4m", "-"]);

        return string.Join(" ", args);
    }

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        var vj = (VideoJob)job;
        var decode = ShellPipeline.FfmpegDecodeCommand(_ffmpegPath, vj);
        ShellPipeline.Apply(psi, $"{decode} | {ShellPipeline.Escape(_x265Path)} {BuildArguments(job)}");
    }

    /// <summary>
    /// Where the raw HEVC goes before it is wrapped. Kept next to the real output so it
    /// lands on the same volume — a temp directory elsewhere would turn the wrap into a
    /// copy across filesystems.
    /// </summary>
    private static string RawStreamPath(VideoJob vj) => vj.OutputFile + ".hevc";

    /// <summary>
    /// Wrap the elementary stream into the container the job asked for.
    ///
    /// This is not cosmetic. Raw HEVC carries no timing whatsoever, so anything reading it
    /// afterwards has to guess the frame rate — FFmpeg guesses 25 — and the video silently
    /// changes duration and drifts out of sync with its audio. Here the source rate is
    /// still known, so it is the one place the guess can be avoided.
    /// </summary>
    protected override async Task OnAfterSuccessAsync(Job job, CancellationToken ct)
    {
        var vj = (VideoJob)job;
        var raw = RawStreamPath(vj);

        if (!File.Exists(raw) || new FileInfo(raw).Length == 0)
            throw new InvalidOperationException(
                $"{DisplayName} reported success but wrote no bitstream to '{raw}'.");

        var rate = await ProbeFrameRateAsync(vj.InputFile, ct)
            // Without a rate there are no timestamps to write, and the mux would fail
            // anyway — better to say why than to emit a file timed at FFmpeg's guess.
            ?? throw new InvalidOperationException(
                $"{DisplayName}: could not read the frame rate of '{Path.GetFileName(vj.InputFile)}', " +
                "so the encoded stream cannot be given correct timestamps.");

        // -r alone is not enough on FFmpeg 8: the raw HEVC demuxer hands the muxer packets
        // with no timestamps at all and Matroska rejects them outright ("Can't write packet
        // with unknown timestamp"). setts synthesises them from the frame index, which also
        // keeps the count exact — muxing to MP4 instead "succeeds" while dropping frames.
        var args = new List<string>
        {
            "-y", "-hide_banner",
            "-r", $"{rate.Num}/{rate.Den}",
            "-f", "hevc", "-i", Quote(raw),
            "-c", "copy",
            "-bsf:v", $"setts=ts=N*{rate.Den}/{rate.Num}/TB",
            Quote(vj.OutputFile)
        };

        var (exitCode, output) = await RunAsync(_ffmpegPath, string.Join(" ", args), ct);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"{DisplayName}: wrapping the HEVC stream into '{Path.GetFileName(vj.OutputFile)}' " +
                $"failed with exit code {exitCode}.\n{output}");

        try { File.Delete(raw); } catch { /* the encode succeeded; a stray temp file is not worth failing over */ }
    }

    /// <summary>
    /// The source frame rate, kept as a rational so 24000/1001 stays exact — rounding it to
    /// 23.976 puts the timestamps a frame out by the end of a feature.
    /// </summary>
    private async Task<(int Num, int Den)?> ProbeFrameRateAsync(string inputFile, CancellationToken ct)
    {
        // r_frame_rate rather than avg_frame_rate: the average is computed across the file
        // and comes back as an awkward ratio for anything variable, while the base rate is
        // what the stream itself declares.
        var (exitCode, output) = await RunAsync(_ffprobePath,
            $"-v error -select_streams v:0 -show_entries stream=r_frame_rate " +
            $"-of default=noprint_wrappers=1:nokey=1 {Quote(inputFile)}", ct);

        if (exitCode != 0) return null;

        var parts = output.Trim().Split('\n')[0].Trim().Split('/');
        if (parts.Length != 2) return null;

        return int.TryParse(parts[0], out var num) && int.TryParse(parts[1], out var den)
               && num > 0 && den > 0
            ? (num, den)
            : null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, string args, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, (await stdout) + (await stderr));
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        // Reading a pipe, x265 has no frame count to work from and reports
        // "1234 frames: 45.67 fps, 1234.56 kb/s".
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
