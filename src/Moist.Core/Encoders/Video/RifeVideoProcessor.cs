using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// AI frame interpolation with RIFE — synthesizes in-between frames to change the frame rate
/// smoothly (24 → 60, buttery slow-mo). Like Real-ESRGAN it only works on still images, so it's
/// frame-based (extract → rife → encode) and therefore a processor, not a filter. For dup/drop or
/// motion-compensated framerate changes that need no model, use the <c>fps</c> / <c>minterpolate</c>
/// filters (surfaced as <c>--fps</c>).
///
/// Handles a <see cref="VideoJob"/> whose <see cref="VideoJob.RifeTargetFps"/> &gt; 0 and which
/// isn't also an AI upscale (the two frame-based paths aren't chained in this version).
/// </summary>
public sealed partial class RifeVideoProcessor : IJobProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _rifePath;
    private readonly string _tempDir;
    private readonly ILogger _logger;
    private const string Model = "rife-v4.6"; // best quality; ships with the tool

    public RifeVideoProcessor(string ffmpegPath, string ffprobePath, string rifePath, string tempDir,
        ILoggerFactory? loggerFactory = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _rifePath = rifePath;
        _tempDir = tempDir;
        _logger = loggerFactory?.CreateLogger<RifeVideoProcessor>() ?? NullLogger<RifeVideoProcessor>.Instance;
    }

    public string Id => "rife";
    public string DisplayName => "RIFE Frame Interpolation";

    public bool CanProcess(Job job)
        => job is VideoJob vj && vj.RifeTargetFps > 0 && string.IsNullOrWhiteSpace(vj.AiUpscaleModel);

    public async Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var vj = (VideoJob)job;
        if (string.IsNullOrWhiteSpace(_rifePath))
            throw new InvalidOperationException(
                "RIFE interpolation needs rife-ncnn-vulkan. Install it from Settings > Prerequisites " +
                "(or run 'moist tools'), then try again.");

        var sw = Stopwatch.StartNew();
        var (srcRate, srcFps, srcFrames) = await ProbeAsync(vj.InputFile, ct);
        srcFrames = RealEsrganVideoProcessor.EstimateTrimmedFrames(vj, srcRate, srcFrames);
        _logger.LogInformation("{Processor} starting job {JobId}: ~{SrcFrames} frames at {SrcFps} fps -> {TargetFps} fps, encoder {Encoder}",
            DisplayName, vj.Id, srcFrames, srcFps, vj.RifeTargetFps, vj.EncoderId);

        var work = Path.Combine(_tempDir, $"rife_{vj.Id}");
        var framesIn = Path.Combine(work, "in");
        var framesOut = Path.Combine(work, "out");
        Directory.CreateDirectory(framesIn);
        Directory.CreateDirectory(framesOut);

        try
        {
            // Extract frames (any FilterScript is applied here), interpolate to the target count,
            // then encode at the requested rate. Reuses Real-ESRGAN's extract/codec builders.
            // The target count is computed from what is actually extracted, at the rate it actually
            // has: a cadence-changing pre-filter (ivtc, fps) or a trim would otherwise make RIFE
            // synthesize the wrong number of frames and the output would run at the wrong speed.
            var postRate = await RealEsrganVideoProcessor.MeasureOutputRateAsync(_ffmpegPath, vj, srcRate, _logger, ct);
            var postFps = ParseFraction(postRate);

            Report(progress, 1, "Extracting frames", sw);
            var extractArgs = RealEsrganVideoProcessor.BuildExtractArgs(vj, Path.Combine(framesIn, "%06d.png"));
            LogCommand(vj, "extract", _ffmpegPath, extractArgs);
            await RunAsync(_ffmpegPath, extractArgs,
                line => RealEsrganVideoProcessor.ReportExtractProgress(line, srcFrames, progress, sw, 1, 7), ct, vj);
            var extracted = Directory.EnumerateFiles(framesIn, "*.png").Count();
            if (extracted == 0)
                throw new InvalidOperationException("Frame extraction produced no frames.");
            var target = TargetFrameCount(extracted, postFps, vj.RifeTargetFps);
            _logger.LogInformation("{Processor} job {JobId}: extracted {Frames} frames at {Rate} fps -> {Target} frames at {TargetFps} fps",
                DisplayName, vj.Id, extracted, postRate, target, vj.RifeTargetFps);

            var rifeArgs = BuildRifeArgs(framesIn, framesOut, ModelDir(), target);
            LogCommand(vj, "interpolate", _rifePath, rifeArgs);
            await RunAsync(_rifePath, rifeArgs,
                line =>
                {
                    var m = PercentRegex().Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pct))
                        Report(progress, 8 + pct / 100.0 * 80, "Interpolating (RIFE)", sw);
                }, ct, vj);

            Report(progress, 90, "Encoding", sw);
            var encodeArgs = BuildEncodeArgs(vj, Path.Combine(framesOut, "%06d.png"));
            LogCommand(vj, "encode", _ffmpegPath, encodeArgs);
            await RunAsync(_ffmpegPath, encodeArgs, null, ct, vj);

            // RIFE's whole purpose is changing the frame count, so verify it landed on the target —
            // a wrong count leaves the video's duration out of step with the untouched audio and the
            // mux drifts silently out of sync. Non-fatal if ffprobe can't count (outFrames == 0).
            if (target > 0)
            {
                var outFrames = await CountFramesAsync(vj.OutputFile, ct);
                if (outFrames > 0 && Math.Abs(outFrames - target) > 1)
                {
                    _logger.LogError("{Processor} job {JobId}: output has {Out} frames but {Target} were targeted", DisplayName, vj.Id, outFrames, target);
                    throw new InvalidOperationException(
                        $"RIFE output has {outFrames} frames but {target} were targeted — refusing a desynced result.");
                }
            }

            _logger.LogInformation("{Processor} job {JobId} finished in {Elapsed:g}", DisplayName, vj.Id, sw.Elapsed);
            Report(progress, 100, "Done", sw);
        }
        finally
        {
            // RIFE keeps no resumable state (the scratch is keyed on the per-build job id), so there
            // is nothing to reuse on a retry — always sweep the work dir instead of leaking GBs of
            // extracted/interpolated PNGs on failure.
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Frames needed to reach the target rate: round(srcFrames * target/src), never fewer than source.</summary>
    internal static long TargetFrameCount(long srcFrames, double srcFps, double targetFps)
        => srcFrames <= 0 || srcFps <= 0 || targetFps <= 0
            ? 0
            : Math.Max(srcFrames, (long)Math.Round(srcFrames * targetFps / srcFps));

    internal static string[] BuildRifeArgs(string inDir, string outDir, string? modelDir, long targetFrames)
    {
        var args = new List<string> { "-i", inDir, "-o", outDir, "-f", "%06d.png" };
        if (targetFrames > 0) args.AddRange(["-n", targetFrames.ToString(CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrWhiteSpace(modelDir)) args.AddRange(["-m", modelDir]);
        return [.. args];
    }

    internal static string[] BuildEncodeArgs(VideoJob vj, string framesPattern)
    {
        var s = vj.Settings;
        var fps = vj.RifeTargetFps.ToString("0.###", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-framerate", fps,   // RIFE preserves resolution; it only changes the frame rate
            "-i", framesPattern,
            "-c:v", RealEsrganVideoProcessor.FinalCodec(vj.EncoderId),
            "-crf", s.Quality.ToString("0.##", CultureInfo.InvariantCulture),
            "-preset", s.Preset.ToString().ToLowerInvariant(),
        };
        // libvpx-vp9 only enters constant-quality mode when -b:v 0 accompanies -crf; without it
        // libvpx targets its default (~256 kbps) and caps the stream regardless of the CRF.
        if (RealEsrganVideoProcessor.FinalCodec(vj.EncoderId) == "libvpx-vp9")
            args.AddRange(["-b:v", "0"]);
        if (s.ResolvePixelFormat(vj.SourceBitDepth) is { } pix) args.AddRange(["-pix_fmt", pix]);
        args.Add("-an");
        args.Add(vj.OutputFile);
        return [.. args];
    }

    private string? ModelDir()
    {
        var dir = Path.GetDirectoryName(_rifePath);
        if (string.IsNullOrEmpty(dir)) return null;
        var m = Path.Combine(dir, Model);
        return Directory.Exists(m) ? m : null;
    }

    private async Task<(string Rate, double Fps, long Frames)> ProbeAsync(string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffprobePath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=r_frame_rate,nb_frames,duration",
            "-of", "default=noprint_wrappers=1", input,
        }) psi.ArgumentList.Add(a);
        var outText = await RunProbeAsync(psi, ct);

        var rate = Match(outText, "r_frame_rate") ?? "25/1";
        var fps = ParseFraction(rate);
        long frames = 0;
        if (long.TryParse(Match(outText, "nb_frames"), out var nb) && nb > 0) frames = nb;
        else if (double.TryParse(Match(outText, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) && dur > 0)
            frames = (long)Math.Round(dur * fps);
        return (rate, fps, frames);
    }

    /// <summary>Decode-count the frames in <paramref name="file"/> (ffprobe -count_frames), used to
    /// confirm the interpolated output has the intended number of frames. 0 means "couldn't count".</summary>
    private async Task<long> CountFramesAsync(string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffprobePath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-v", "error", "-select_streams", "v:0", "-count_frames",
            "-show_entries", "stream=nb_read_frames", "-of", "default=nokey=1:noprint_wrappers=1", file,
        }) psi.ArgumentList.Add(a);
        var outText = await RunProbeAsync(psi, ct);
        return long.TryParse(outText.Trim(), out var n) ? n : 0;
    }

    /// <summary>Run a one-shot ffprobe, draining <b>both</b> stdout and stderr concurrently so a
    /// chatty child (e.g. -count_frames spewing decode errors on a corrupt file) can't fill the
    /// stderr pipe and deadlock us; the child is killed if we're cancelled. Returns stdout.</summary>
    private async Task<string> RunProbeAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(_ffprobePath)}.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.WhenAll(outTask, errTask);
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch { /* best effort */ } }
            throw;
        }
        return await outTask;
    }

    private void LogCommand(Job? logJob, string label, string exe, string[] args)
    {
        logJob?.AppendLog($"[{label}] {Path.GetFileName(exe)} {string.Join(' ', args)}");
        _logger.LogInformation("{Processor} [{Step}] {Exe} {Args}", DisplayName, label, Path.GetFileName(exe), string.Join(' ', args));
    }

    /// <summary>Same contract as Real-ESRGAN's runner: every output line is a heartbeat for the
    /// stall watchdog and lands in the job log; a silent tool is killed, not waited on forever.</summary>
    private async Task RunAsync(string exe, string[] args, Action<string>? onLine, CancellationToken ct, Job? logJob = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        var lastOutputTicks = DateTime.UtcNow.Ticks;
        void Sink(string? line)
        {
            if (line is null) return;
            Volatile.Write(ref lastOutputTicks, DateTime.UtcNow.Ticks);
            onLine?.Invoke(line);
            logJob?.AppendLog(line);
            lock (tail) { tail.Enqueue(line); while (tail.Count > 12) tail.Dequeue(); }
        }
        p.OutputDataReceived += (_, e) => Sink(e.Data);
        p.ErrorDataReceived += (_, e) => Sink(e.Data);
        if (!p.Start())
        {
            _logger.LogError("{Processor}: failed to start {Exe}", DisplayName, exe);
            throw new InvalidOperationException($"Failed to start {Path.GetFileName(exe)}.");
        }
        _logger.LogDebug("{Processor}: {Exe} started (PID {Pid})", DisplayName, Path.GetFileName(exe), p.Id);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await ProcessWatchdog.WaitForExitOrStallAsync(p, () => Volatile.Read(ref lastOutputTicks),
                ProcessWatchdog.DefaultStallTimeout, $"{DisplayName} ({Path.GetFileName(exe)})", _logger, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Processor}: cancelled, killing {Exe}", DisplayName, Path.GetFileName(exe));
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        if (p.ExitCode != 0)
        {
            string tailText;
            lock (tail) tailText = string.Join("\n", tail);
            _logger.LogError("{Processor}: {Exe} exited with code {Code}\n{Tail}", DisplayName, Path.GetFileName(exe), p.ExitCode, tailText);
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with code {p.ExitCode}.\n{tailText}");
        }
    }

    private static void Report(IProgress<JobProgress> progress, double percent, string status, Stopwatch sw)
        => progress.Report(new JobProgress { Percentage = Math.Clamp(percent, 0, 100), Elapsed = sw.Elapsed, StatusLine = status });

    private static string? Match(string text, string key)
    {
        var m = Regex.Match(text, $@"{Regex.Escape(key)}=(.+)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static double ParseFraction(string frac)
    {
        var parts = frac.Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0
            ? n / d : 25.0;
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)%")]
    private static partial Regex PercentRegex();
}
