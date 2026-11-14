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
    private readonly string? _mkvmergePath;
    private readonly Stopwatch _stopwatch = new();

    /// <summary>The wrap decision, made once per job so both passes of a two-pass encode agree
    /// on B-frames (the pass-1 stats must describe the stream pass 2 produces).</summary>
    private bool? _useMkvmerge;

    public override string Id => "cli-x265";
    public override string DisplayName => "x265 (CLI)";

    public X265CliProcessor(string x265Path, string ffmpegPath, string ffprobePath = "ffprobe", string? mkvmergePath = null)
    {
        _x265Path = x265Path;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _mkvmergePath = string.IsNullOrWhiteSpace(mkvmergePath) ? null : mkvmergePath;
    }

    /// <summary>Whether this job wraps with mkvmerge (keeping B-frames) or with FFmpeg (no B-frames).</summary>
    internal bool UsesMkvmerge(VideoJob vj)
    {
        if (_useMkvmerge is { } decided) return decided;
        var mode = (vj.Settings as X265Settings)?.StreamWrap ?? X265StreamWrap.NoBFrames;
        _useMkvmerge = mode switch
        {
            X265StreamWrap.NoBFrames => false,
            X265StreamWrap.Mkvmerge => MkvmergeWorks()
                ? true
                : throw new InvalidOperationException(
                    $"{DisplayName}: the stream wrap is set to mkvmerge, but mkvmerge ('{_mkvmergePath ?? "mkvmerge"}') " +
                    "could not be run. Install MKVToolNix (Settings -> Tools can download it on Windows), set its path, " +
                    "or choose the 'No B-frames' wrap."),
            _ => MkvmergeWorks(),
        };
        return _useMkvmerge.Value;
    }

    /// <summary>Overridable so unit tests can decide without launching a process.</summary>
    protected virtual bool MkvmergeWorks()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_mkvmergePath ?? "mkvmerge", "--version")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
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

        // FFmpeg can only number a raw HEVC stream's packets in decode order, which is display
        // order only without B-frames (see X265StreamWrap). Placed before CustomArgs so an explicit
        // --bframes there still wins - and then mkvmerge is the wrap that keeps it correct.
        if (!UsesMkvmerge(vj))
            args.AddRange(["--bframes", "0"]);

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
    /// The two-pass analysis pass points the output at the null device; x265 accepts NUL /
    /// /dev/null directly and writes only its stats file, so there is no bitstream to wrap.
    /// </summary>
    private static bool IsNullDevice(string path) =>
        string.Equals(path, "NUL", StringComparison.OrdinalIgnoreCase) ||
        path == "/dev/null";

    /// <summary>
    /// Where the raw HEVC goes before it is wrapped. Kept next to the real output so it
    /// lands on the same volume — a temp directory elsewhere would turn the wrap into a
    /// copy across filesystems. On the analysis pass the output is the null device, so -o
    /// points straight at it rather than trying to create "/dev/null.hevc".
    /// </summary>
    private static string RawStreamPath(VideoJob vj) =>
        IsNullDevice(vj.OutputFile) ? vj.OutputFile : vj.OutputFile + ".hevc";

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

        // Two-pass analysis (pass 1) writes to the null device: no bitstream is produced,
        // so there is nothing to wrap. CommandLineProcessor calls this after every run.
        if (IsNullDevice(vj.OutputFile)) return;

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

        if (UsesMkvmerge(vj))
            await WrapWithMkvmergeAsync(vj, raw, rate, ct);
        else
            await WrapWithFfmpegAsync(vj, raw, rate, ct);

        try { File.Delete(raw); } catch { /* the encode succeeded; a stray temp file is not worth failing over */ }
    }

    /// <summary>
    /// mkvmerge reads the HEVC picture order and writes correct presentation timestamps for a
    /// B-frame stream; --default-duration gives it the rate the raw stream doesn't carry.
    /// Exit code 1 is "finished with warnings" and still a good file; 2 is an error.
    /// </summary>
    private async Task WrapWithMkvmergeAsync(VideoJob vj, string raw, (int Num, int Den) rate, CancellationToken ct)
    {
        var matroska = IsMatroska(vj.OutputFile);
        var target = matroska ? vj.OutputFile : vj.OutputFile + ".wrap.mkv";
        var args = $"-q -o {Quote(target)} --default-duration 0:{rate.Num}/{rate.Den}p {Quote(raw)}";
        var (exitCode, output) = await RunAsync(_mkvmergePath ?? "mkvmerge", args, ct);
        if (exitCode is not (0 or 1))
            throw new InvalidOperationException(
                $"{DisplayName}: mkvmerge failed to wrap the HEVC stream (exit {exitCode}).\n{output}");

        if (!matroska)
        {
            // The pipeline's intermediates are Matroska, but stay correct for any other container:
            // a stream copy keeps mkvmerge's timestamps.
            var (code, log) = await RunAsync(_ffmpegPath, $"-y -hide_banner -i {Quote(target)} -c copy {Quote(vj.OutputFile)}", ct);
            try { File.Delete(target); } catch { }
            if (code != 0)
                throw new InvalidOperationException($"{DisplayName}: remuxing the wrapped stream failed (exit {code}).\n{log}");
        }
        await VerifyTimingAsync(vj.OutputFile, rate, ct, throwOnMismatch: true);
    }

    /// <summary>
    /// FFmpeg wrap for a stream with no B-frames, where decode order is display order and the
    /// packet index is the frame index. The raw demuxer gives packets no timestamps at all on
    /// FFmpeg 8 (Matroska refuses them) so setts synthesises them - but FFmpeg before 7 hands the
    /// filter's output to the Matroska muxer in milliseconds regardless of the filter's time base,
    /// and the modern expression came out at ~1 ms per frame there. Rather than parse version
    /// strings, wrap with the modern expression, verify the duration against packets / rate, and
    /// fall back to the millisecond form if it's wrong. Muxing to MP4 instead is not an option:
    /// it "succeeds" while dropping frames.
    /// </summary>
    private async Task WrapWithFfmpegAsync(VideoJob vj, string raw, (int Num, int Den) rate, CancellationToken ct)
    {
        string[] expressions =
        [
            $"setts=ts=N*{rate.Den}/{rate.Num}/TB",          // FFmpeg 7+: exact, in the stream time base
            $"setts=ts=N*{rate.Den * 1000L}/{rate.Num}",      // FFmpeg 5/6: Matroska milliseconds
        ];

        string? lastProblem = null;
        foreach (var expr in expressions)
        {
            var args = string.Join(" ",
                "-y", "-hide_banner", "-r", $"{rate.Num}/{rate.Den}", "-f", "hevc", "-i", Quote(raw),
                "-c", "copy", "-bsf:v", Quote(expr), Quote(vj.OutputFile));
            var (exitCode, output) = await RunAsync(_ffmpegPath, args, ct);
            if (exitCode != 0)
            {
                lastProblem = $"exit code {exitCode}\n{output}";
                continue;
            }
            lastProblem = await VerifyTimingAsync(vj.OutputFile, rate, ct, throwOnMismatch: false);
            if (lastProblem is null) return;
        }
        throw new InvalidOperationException(
            $"{DisplayName}: wrapping the HEVC stream into '{Path.GetFileName(vj.OutputFile)}' failed: {lastProblem}");
    }

    /// <summary>
    /// The wrapped file's duration must equal its frame count over the rate. This is what caught
    /// FFmpeg 5.1 writing 2 s of video as 0.09 s; a frame count alone can't see it.
    /// Returns null when the timing is right, otherwise the problem (or throws).
    /// </summary>
    private async Task<string?> VerifyTimingAsync(string file, (int Num, int Den) rate, CancellationToken ct, bool throwOnMismatch)
    {
        var (code, output) = await RunAsync(_ffprobePath,
            $"-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets:format=duration " +
            $"-of default=noprint_wrappers=1 {Quote(file)}", ct);
        if (code != 0) return Fail($"could not probe the wrapped file (exit {code})");

        long packets = 0; double duration = 0;
        foreach (var line in output.Split('\n'))
        {
            var kv = line.Trim().Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "nb_read_packets") long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out packets);
            if (kv[0] == "duration") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }
        if (packets <= 0) return Fail("the wrapped file has no video packets");

        var frame = (double)rate.Den / rate.Num;
        var expected = packets * frame;
        // Containers round and may or may not count the last frame's duration: allow two frames.
        return Math.Abs(duration - expected) <= Math.Max(2 * frame, 0.05)
            ? null
            : Fail($"timestamps are wrong: {packets} frames at {rate.Num}/{rate.Den} should last {expected:F3}s, the file says {duration:F3}s");

        string Fail(string why) => throwOnMismatch
            ? throw new InvalidOperationException($"{DisplayName}: {why}")
            : why;
    }

    private static bool IsMatroska(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mka" or ".webm";

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
