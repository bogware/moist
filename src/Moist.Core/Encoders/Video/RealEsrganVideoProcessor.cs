using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Upscales a video with Real-ESRGAN — a deep super-resolution model that only processes still
/// images, so this is frame-based, not a filter: extract cleaned frames → run the model → encode.
///
/// It's the max-quality, opt-in path (see the GPU-shader "superres" filter for the fast everyday
/// one). Real-ESRGAN runs at a few frames a second and every upscaled frame is a multi-megabyte
/// image, so this is for short or precious sources — hence the frame-count safety cap.
///
/// Handles a <see cref="VideoJob"/> whose <see cref="VideoJob.AiUpscaleModel"/> is set; it still
/// names a real encoder (x265, …) for the final step, so it's registered <b>before</b> the normal
/// video processors and wins via <see cref="CanProcess"/>.
/// </summary>
public sealed partial class RealEsrganVideoProcessor : IJobProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly string _realesrganPath;
    private readonly string _tempDir;
    private readonly ILogger _logger;

    public RealEsrganVideoProcessor(string ffmpegPath, string ffprobePath, string realesrganPath, string tempDir,
        ILoggerFactory? loggerFactory = null)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _realesrganPath = realesrganPath;
        _tempDir = tempDir;
        _logger = loggerFactory?.CreateLogger<RealEsrganVideoProcessor>() ?? NullLogger<RealEsrganVideoProcessor>.Instance;
    }

    public string Id => "realesrgan";
    public string DisplayName => "Real-ESRGAN AI Upscale";

    public bool CanProcess(Job job) => job is VideoJob vj && !string.IsNullOrWhiteSpace(vj.AiUpscaleModel);

    /// <summary>Map a friendly model id to the ncnn model name shipped with the tool.</summary>
    public static string ResolveModel(string? model) => (model ?? "").Trim().ToLowerInvariant() switch
    {
        "anime" or "animevideov3" or "anime-video" => "realesr-animevideov3",
        _ => "realesrgan-x4plus", // general / live action
    };

    public async Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var vj = (VideoJob)job;

        if (string.IsNullOrWhiteSpace(_realesrganPath))
            throw new InvalidOperationException(
                "Real-ESRGAN AI upscale needs realesrgan-ncnn-vulkan. Install it from Settings > Prerequisites " +
                "(or run 'moist tools'), or set its path, then try again.");

        var stopwatch = Stopwatch.StartNew();
        var (sourceRate, estimatedFrames) = await ProbeAsync(vj.InputFile, ct);
        estimatedFrames = EstimateTrimmedFrames(vj, sourceRate, estimatedFrames);
        _logger.LogInformation("{Processor} starting job {JobId}: model {Model}, ~{Frames} frames at {Fps} fps, encoder {Encoder}",
            DisplayName, vj.Id, ResolveModel(vj.AiUpscaleModel), estimatedFrames, sourceRate, vj.EncoderId);

        // Deterministic work dir keyed on source + settings, so an interrupted run resumes (finished
        // segments are reused) rather than starting the whole slog over.
        var work = Path.Combine(_tempDir, $"realesrgan_{WorkKey(vj)}");
        var framesIn = Path.Combine(work, "in");
        // The output frame rate is *measured* during extraction (the pre-clean may change the
        // cadence — ivtc, fps, decimate) and recorded here so a resume that skips extraction
        // still encodes at the right rate.
        Directory.CreateDirectory(framesIn);

        var success = false;
        try
        {
            // The pre-clean may change the cadence (ivtc 59.94 → 23.976, fps=, decimate), so the
            // rate the frames are encoded at is measured through that same chain — encoding ivtc'd
            // frames at the *source* rate played them at 2.5× speed against the untouched audio.
            var frameRate = await MeasureOutputRateAsync(_ffmpegPath, vj, sourceRate, _logger, ct);

            // 1) Extract cleaned frames once (FilterScript = the pre-clean). Skipped on resume.
            if (!Directory.EnumerateFiles(framesIn, "*.png").Any())
            {
                Report(progress, 1, "Extracting frames", stopwatch);
                var extractArgs = BuildExtractArgs(vj, Path.Combine(framesIn, "%06d.png"));
                LogCommand(vj, "extract", _ffmpegPath, extractArgs);
                await RunAsync(_ffmpegPath, extractArgs,
                    line => ReportExtractProgress(line, estimatedFrames, progress, stopwatch), ct, vj);
            }
            else
            {
                _logger.LogInformation("{Processor} resuming job {JobId}: reusing extracted frames in {Dir}", DisplayName, vj.Id, framesIn);
            }
            var frames = Directory.GetFiles(framesIn, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (frames.Count == 0) throw new InvalidOperationException("Frame extraction produced no frames.");

            // 2+3) Small clips go all-at-once; longer ones stream in windows so the multi-MB upscaled
            //       frames never all hit disk at once — this is what lifts the old frame cap.
            if (frames.Count <= WindowFrames)
                await UpscaleSegmentAsync(vj, work, framesIn, 1, frameRate, vj.OutputFile, progress, stopwatch, 8, 96, ct);
            else
                await WindowedAsync(vj, work, frames, frameRate, progress, stopwatch, ct);

            // 4) Verify nothing was lost — a re-encode that silently drops frames desyncs the audio.
            var outFrames = await CountFramesAsync(vj.OutputFile, ct);
            if (outFrames > 0 && Math.Abs(outFrames - frames.Count) > 1)
            {
                _logger.LogError("{Processor} job {JobId}: output has {Out} frames but {In} were upscaled", DisplayName, vj.Id, outFrames, frames.Count);
                throw new InvalidOperationException(
                    $"Real-ESRGAN output has {outFrames} frames but {frames.Count} were upscaled — refusing a desynced result.");
            }

            _logger.LogInformation("{Processor} job {JobId} finished: {Frames} frames in {Elapsed:g}", DisplayName, vj.Id, frames.Count, stopwatch.Elapsed);
            Report(progress, 100, "Done", stopwatch);
            success = true;
        }
        finally
        {
            // Keep the scratch on failure so a re-run resumes; clear it once we have a good result.
            if (success) { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
        }
    }

    private const int WindowFrames = 900;

    /// <summary>Stable key for the work dir: the same source + settings resume the same scratch.</summary>
    internal static string WorkKey(VideoJob vj)
    {
        // The finish shapes every encoded segment, so a resumed run must not reuse segments
        // finished differently.
        var raw = $"{vj.InputFile}|{vj.AiUpscaleModel}|{vj.AiUpscaleHeight}|{vj.EncoderId}|{vj.Settings.Quality}|{vj.FilterScript}|{vj.AiFinishFilterScript}|{vj.Settings.Tune}"
                + $"|{vj.TrimStart?.Ticks}|{vj.TrimDuration?.Ticks}|{vj.TrimFrames}";
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..12];
    }

    /// <summary>Upscale every PNG in <paramref name="framesInDir"/> and encode straight to
    /// <paramref name="outFile"/>. The whole-clip path, reused per window over a copied subset.</summary>
    private async Task UpscaleSegmentAsync(VideoJob vj, string work, string framesInDir, long startNumber,
        string frameRate, string outFile, IProgress<JobProgress> progress, Stopwatch sw, double pctStart, double pctEnd, CancellationToken ct)
    {
        var outDir = Path.Combine(work, "up");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        var upscaleArgs = BuildRealesrganArgs(vj, framesInDir, outDir, ModelsDir());
        LogCommand(vj, "upscale", _realesrganPath, upscaleArgs);
        // realesrgan-ncnn-vulkan prints per-IMAGE tile progress (0→25→50→75, each value twice), so
        // its percentage sawtooths and can't drive the bar directly — mapping it straight onto the
        // window span made the bar oscillate. Count an image as done each time the value wraps back
        // down, and report from the running count so progress is monotonic and smooth.
        var totalFrames = Directory.EnumerateFiles(framesInDir, "*.png").Count();
        var completed = 0;
        var lastPct = -1.0;
        await RunAsync(_realesrganPath, upscaleArgs,
            line =>
            {
                var m = PercentRegex().Match(line);
                if (!m.Success || !double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pct)) return;
                if (pct < lastPct) completed = Math.Min(completed + 1, Math.Max(totalFrames, 1));
                lastPct = pct;
                var frac = totalFrames > 0 ? (double)completed / totalFrames : 0;
                Report(progress, pctStart + frac * (pctEnd - pctStart), "Upscaling (Real-ESRGAN)", sw);
            }, ct, vj);

        var encodeArgs = BuildEncodeArgs(vj, Path.Combine(outDir, "%06d.jpg"), frameRate, outFile, startNumber);
        LogCommand(vj, "encode", _ffmpegPath, encodeArgs);
        await RunAsync(_ffmpegPath, encodeArgs, null, ct, vj);
        Directory.Delete(outDir, recursive: true);
    }

    /// <summary>Stream a long source in windows: copy a slice of frames, upscale+encode it to a
    /// segment, drop the scratch, repeat — then concat. Peak upscaled-frame disk is one window.</summary>
    private async Task WindowedAsync(VideoJob vj, string work, List<string> frames, string frameRate,
        IProgress<JobProgress> progress, Stopwatch sw, CancellationToken ct)
    {
        var segDir = Path.Combine(work, "segments");
        Directory.CreateDirectory(segDir);
        var windows = PlanWindows(frames.Count, WindowFrames);
        var segments = new List<string>();

        for (var i = 0; i < windows.Count; i++)
        {
            var (start, count) = windows[i];
            var seg = Path.Combine(segDir, $"seg_{i:D5}.mkv");

            // Resume: reuse a segment already fully encoded to the right length.
            if (File.Exists(seg) && await CountFramesAsync(seg, ct) == count)
            {
                _logger.LogInformation("{Processor} job {JobId}: window {Window}/{Windows} already encoded, reusing {Segment}",
                    DisplayName, vj.Id, i + 1, windows.Count, Path.GetFileName(seg));
                segments.Add(seg);
                continue;
            }

            var winIn = Path.Combine(work, "win");
            if (Directory.Exists(winIn)) Directory.Delete(winIn, recursive: true);
            Directory.CreateDirectory(winIn);
            // Copy (don't move) so framesIn stays intact for a robust resume.
            for (var f = start; f < start + count; f++)
                File.Copy(frames[f], Path.Combine(winIn, Path.GetFileName(frames[f])), overwrite: true);

            _logger.LogInformation("{Processor} job {JobId}: window {Window}/{Windows} (frames {Start}-{End})",
                DisplayName, vj.Id, i + 1, windows.Count, start, start + count - 1);
            var baseP = 5 + (double)i / windows.Count * 92;
            var spanP = 92.0 / windows.Count;
            await UpscaleSegmentAsync(vj, work, winIn, FrameNumber(frames[start]), frameRate, seg, progress, sw, baseP, baseP + spanP * 0.9, ct);
            Directory.Delete(winIn, recursive: true);
            segments.Add(seg);
            Report(progress, baseP + spanP, $"Window {i + 1}/{windows.Count}", sw);
        }

        // Concat the segments — same encoder settings throughout, so a stream copy is exact.
        var listPath = Path.Combine(work, "concat.txt");
        await File.WriteAllTextAsync(listPath,
            string.Join("\n", segments.Select(s => $"file '{s.Replace('\\', '/')}'")), ct);
        var concatArgs = new[] { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", vj.OutputFile };
        LogCommand(vj, "concat", _ffmpegPath, concatArgs);
        await RunAsync(_ffmpegPath, concatArgs, null, ct, vj);
    }

    /// <summary>Split <paramref name="total"/> frames into contiguous windows of at most <paramref name="size"/>.</summary>
    internal static List<(int Start, int Count)> PlanWindows(int total, int size)
    {
        var windows = new List<(int, int)>();
        for (var start = 0; start < total; start += size)
            windows.Add((start, Math.Min(size, total - start)));
        return windows;
    }

    private static long FrameNumber(string path)
        => long.TryParse(Path.GetFileNameWithoutExtension(path), out var n) ? n : 1;

    // === Command builders (pure; unit-tested without launching anything) ===

    internal static string[] BuildExtractArgs(VideoJob vj, string framesPattern)
    {
        // -progress on stdout gives the stall watchdog a heartbeat and the bar a frame count; the
        // human stats line is suppressed so the two don't interleave in the job log.
        var args = new List<string> { "-y", "-hide_banner", "-nostats", "-progress", "pipe:1" };
        // Trims are not optional: snippet previews and quality probes encode a piece of the source,
        // and ignoring them here would upscale the whole film for a 3-second preview.
        if (vj.TrimStart is { } trimStart)
            args.AddRange(["-ss", trimStart.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", vj.InputFile]);
        // Pre-clean before the AI upscale — an AI model sharpens block noise as happily as detail.
        if (!string.IsNullOrWhiteSpace(vj.FilterScript))
            args.AddRange(["-vf", vj.FilterScript]);
        if (vj.TrimFrames is { } trimFrames)
            args.AddRange(["-frames:v", trimFrames.ToString(CultureInfo.InvariantCulture)]);
        else if (vj.TrimDuration is { } trimDuration)
            args.AddRange(["-t", trimDuration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);
        // Passthrough: exactly one PNG per (filtered) frame, so the count can be verified later.
        // -fps_mode, not -vsync: FFmpeg 9 removed -vsync ("Unrecognized option") — including the
        // pinned build moist installs and the Docker image ships — and -fps_mode exists since 5.1.
        args.AddRange(["-fps_mode", "passthrough", framesPattern]);
        return [.. args];
    }

    internal static string[] BuildRealesrganArgs(VideoJob vj, string inDir, string outDir, string? modelsDir)
    {
        var args = new List<string>
        {
            "-i", inDir, "-o", outDir,
            "-n", ResolveModel(vj.AiUpscaleModel),
            "-f", "jpg", // JPEG intermediates: a 4× frame is multi-MB as PNG; JPEG q-default cuts that ~5×.
        };
        if (!string.IsNullOrWhiteSpace(modelsDir))
            args.AddRange(["-m", modelsDir]);
        return [.. args];
    }

    /// <summary>The post-upscale finish: the job's own, or the default light CAS + deband.</summary>
    internal static string FinishChain(VideoJob vj)
        => string.IsNullOrWhiteSpace(vj.AiFinishFilterScript)
            ? "cas=strength=0.4,deband=range=16:blur=1"
            : vj.AiFinishFilterScript.Trim().Trim(',');

    internal static string[] BuildEncodeArgs(VideoJob vj, string framesPattern, string frameRate, string outFile, long startNumber = 1)
    {
        var s = vj.Settings;
        var height = vj.AiUpscaleHeight > 0 ? vj.AiUpscaleHeight : 1080;

        var args = new List<string>
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-framerate", frameRate,          // the rate measured after the pre-clean, so playback speed is preserved
            "-start_number", startNumber.ToString(CultureInfo.InvariantCulture),
            "-i", framesPattern,
            // Scale the AI-upscaled frames to the exact target height (aspect preserved), then the
            // finish: the profile's own (sharpen, contrast, film grain…) or, by default, a light CAS
            // + deband — gentle because the model already added detail.
            "-vf", $"scale=-2:{height}:flags=spline,{FinishChain(vj)}",
            "-c:v", FinalCodec(vj.EncoderId),
        };

        // libvpx-vp9 only enters constant-quality mode when -b:v 0 accompanies -crf; without it
        // libvpx runs constrained-quality against its default target (~256 kbps) and caps the
        // stream regardless of the CRF, so an AI-upscaled VP9 comes out blocky.
        if (FinalCodec(vj.EncoderId) == "libvpx-vp9")
            args.AddRange(["-b:v", "0"]);

        // Rate control: CRF/quality is the natural fit here; anything else falls back to CRF.
        var q = s.Quality.ToString("0.##", CultureInfo.InvariantCulture);
        args.AddRange(["-crf", q]);
        args.AddRange(["-preset", s.Preset.ToString().ToLowerInvariant()]);

        // The tune matters most on exactly this path: tune=grain is what keeps x264/x265 from
        // smoothing away the film grain a finish just laid down. It used to be dropped here.
        if (s.Tune != VideoTune.None && FinalCodec(vj.EncoderId) is "libx264" or "libx265")
            args.AddRange(["-tune", s.Tune.ToString().ToLowerInvariant()]);

        if (s.ResolvePixelFormat(vj.SourceBitDepth) is { } pixFmt)
            args.AddRange(["-pix_fmt", pixFmt]);

        args.Add("-an");
        // Write to the caller's target — a per-window segment in the windowed path, or the final
        // output for a whole-clip encode. Using vj.OutputFile unconditionally made every window
        // overwrite the final file and left segments/ empty, so the concat step failed at ~99%.
        args.Add(outFile);
        return [.. args];
    }

    /// <summary>
    /// Measure the frame rate coming <i>out of</i> the pre-clean chain by running a dozen frames
    /// through it with <c>settb=AVTB,showinfo</c> appended: showinfo then prints each output
    /// frame's pts in microseconds, and the median spacing is the frame period. Exact for any
    /// constant-rate output (ivtc, fps=, decimate, doubling), version-independent, sub-second.
    /// Falls back to the source rate — with a warning — if ffmpeg fails or prints too little.
    /// Shared with <see cref="RifeVideoProcessor"/>.
    /// </summary>
    internal static async Task<string> MeasureOutputRateAsync(string ffmpegPath, VideoJob vj, string sourceRate, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in BuildRateProbeArgs(vj)) psi.ArgumentList.Add(a);

        string stderr;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(ffmpegPath)}.");
            var outTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var errTask = p.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await Task.WhenAll(outTask, errTask);
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException("frame-rate probe produced no result within 90s");
            }
            stderr = await errTask;
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with code {p.ExitCode}: {stderr[^Math.Min(stderr.Length, 400)..]}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not measure the post-filter frame rate for {Input}; using the source rate {Rate}", vj.InputFile, sourceRate);
            return sourceRate;
        }

        var sample = MeasureFromShowinfo(stderr.Split('\n'));
        var measured = sample?.Fps;
        var rate = ResolveOutputFrameRate(sourceRate, measured, sample?.Uniform ?? true);
        if (measured is null)
            logger.LogWarning("Frame-rate probe for {Input} printed too few frames; using the source rate {Rate}", vj.InputFile, sourceRate);
        else
            logger.LogInformation("Post-filter frame rate for {Input}: {Rate} (source {Source}, measured {Measured:F4} fps)",
                Path.GetFileName(vj.InputFile), rate, sourceRate, measured.Value);
        return rate;
    }

    /// <summary>The probe command: the same seek and pre-clean as the extract step, a dozen output
    /// frames into the null muxer, with pts rescaled to microseconds and printed per frame.</summary>
    internal static string[] BuildRateProbeArgs(VideoJob vj)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "info" };
        if (vj.TrimStart is { } trimStart)
            args.AddRange(["-ss", trimStart.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", vj.InputFile]);
        var chain = string.IsNullOrWhiteSpace(vj.FilterScript) ? "" : vj.FilterScript + ",";
        args.AddRange(["-vf", chain + "settb=AVTB,showinfo", "-fps_mode", "passthrough", "-frames:v", RateProbeFrames, "-f", "null", "-"]);
        return [.. args];
    }

    /// <summary>Frames the rate probe pushes through the chain. Enough to span several 5-frame
    /// telecine cycles, so an uneven timestamp pattern still averages out (see <see cref="MeasureFromShowinfo"/>).</summary>
    internal const string RateProbeFrames = "48";

    /// <summary>Frame rate from showinfo's per-frame <c>pts:</c> values; see <see cref="MeasureFromShowinfo"/>.</summary>
    internal static double? RateFromShowinfo(IEnumerable<string> lines) => MeasureFromShowinfo(lines)?.Fps;

    /// <summary>Frame rate from showinfo's per-frame <c>pts:</c> values (microseconds, courtesy of
    /// <c>settb=AVTB</c>). Null with fewer than three frames.
    /// <para>When the frames are evenly spaced (FFmpeg 6+ <c>decimate</c> retimes its output) the
    /// rate is one million over the median spacing, which is exact. FFmpeg 5.1's <c>decimate</c> —
    /// what Debian and the Docker image ship — drops one frame in five but leaves the survivors on
    /// the 30i grid (33/33/33/67 ms), so the median reads 29.97 instead of 23.976. Uneven spacing is
    /// therefore reported as <c>Uniform = false</c> with the average over the whole span, and
    /// <see cref="ResolveOutputFrameRate"/> snaps that looser figure to a simple multiple of the
    /// source rate.</para></summary>
    internal static (double Fps, bool Uniform)? MeasureFromShowinfo(IEnumerable<string> lines)
    {
        var pts = new List<long>();
        foreach (var line in lines)
        {
            var m = ShowinfoPtsRegex().Match(line);
            if (m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                pts.Add(v);
        }
        if (pts.Count < 3) return null;
        var deltas = new List<long>();
        for (var i = 1; i < pts.Count; i++)
            if (pts[i] > pts[i - 1]) deltas.Add(pts[i] - pts[i - 1]);
        if (deltas.Count < 2) return null;
        deltas.Sort();
        var median = deltas[deltas.Count / 2];
        // Spacing within ±10% of the median everywhere = constant rate; anything else is a cadence
        // pattern in the timestamps (or a dropout), where only the long-run average is meaningful.
        var uniform = deltas[0] >= median * 0.9 && deltas[^1] <= median * 1.1;
        if (uniform) return (1_000_000.0 / median, true);
        var span = pts.Max() - pts.Min();
        return span > 0 ? ((pts.Count - 1) * 1_000_000.0 / span, false) : null;
    }

    [GeneratedRegex(@"showinfo.*\bpts:\s*(-?\d+)")]
    private static partial Regex ShowinfoPtsRegex();

    /// <summary>
    /// The frame rate to encode the extracted frames at: the measured rate snapped to an exact
    /// rational when it is one — a simple multiple of the source rate (ivtc's 2/5, decimate's 4/5,
    /// a doubling) or a broadcast standard — so 23.976 comes out as 24000/1001, not 23.976 rounded.
    /// 24 and 24000/1001 are 0.1% apart, enough to drift seconds over a film, so the snap tolerance
    /// is far tighter than that; a rate that snaps to nothing is used as measured, to 0.001 fps.
    /// Without a measurement (the probe failed or produced too few frames) the source rate stands.
    /// </summary>
    internal static string ResolveOutputFrameRate(string sourceRate, double? measuredFps, bool uniform = true)
    {
        if (measuredFps is not { } fps || !double.IsFinite(fps) || fps <= 0) return sourceRate;
        var (num, den) = ParseRational(sourceRate);

        var candidates = new List<(long Num, long Den)>();
        if (num > 0 && den > 0)
            for (var k = 1; k <= 6; k++)
                for (var m = 1; m <= 6; m++)
                    candidates.Add((num * k, den * m));
        candidates.AddRange([(24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (48, 1), (50, 1),
                             (60000, 1001), (60, 1), (100, 1), (120000, 1001), (120, 1)]);

        (long Num, long Den) best = default;
        var bestErr = double.MaxValue;
        foreach (var c in candidates)
        {
            var err = Math.Abs((double)c.Num / c.Den - fps) / fps;
            if (err < bestErr) { bestErr = err; best = c; }
        }
        if (bestErr < 1e-4)
        {
            var g = Gcd(best.Num, best.Den);
            return $"{best.Num / g}/{best.Den / g}";
        }

        // An average over uneven timestamps is only good to a percent or two, so it can't tell 24
        // from 24000/1001 — but the filters that produce uneven spacing (decimate on old FFmpeg)
        // only ever drop a fixed share of the source frames, so the true rate is a simple multiple
        // of the source. Take the nearest one within 3%.
        if (!uniform && num > 0 && den > 0)
        {
            (long Num, long Den) near = default;
            var nearErr = double.MaxValue;
            for (var k = 1; k <= 6; k++)
                for (var m = 1; m <= 6; m++)
                {
                    var err = Math.Abs((double)(num * k) / (den * m) - fps) / fps;
                    if (err < nearErr) { nearErr = err; near = (num * k, den * m); }
                }
            if (nearErr < 0.03)
            {
                var g = Gcd(near.Num, near.Den);
                return $"{near.Num / g}/{near.Den / g}";
            }
        }
        return $"{(long)Math.Round(fps * 1000)}/1000";
    }

    private static (long Num, long Den) ParseRational(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0)
            return (n, d);
        if (double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
            return ((long)Math.Round(f * 1000), 1000);
        return (0, 0);
    }

    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Max(a, 1); }

    /// <summary>The frame estimate the extract progress bar is measured against, honouring trims.</summary>
    internal static long EstimateTrimmedFrames(VideoJob vj, string sourceRate, long sourceFrames)
    {
        if (vj.TrimFrames is { } frames && frames > 0) return frames;
        if (vj.TrimDuration is { } duration && duration > TimeSpan.Zero)
            return (long)Math.Round(duration.TotalSeconds * ParseFraction(sourceRate));
        return sourceFrames;
    }

    /// <summary>Final-encode codec for the AI-upscale path. Hardware ids map to their software
    /// equivalent — the upscale is the bottleneck, so a CPU encode of the frames is fine.</summary>
    internal static string FinalCodec(string encoderId) => encoderId switch
    {
        var id when id.Contains("x264") || id.Contains("h264") => "libx264",
        var id when id.Contains("svtav1") || id.Contains("av1") => "libsvtav1",
        var id when id.Contains("vp9") => "libvpx-vp9",
        _ => "libx265", // x265 / hevc / anything else
    };

    private string? ModelsDir()
    {
        var dir = Path.GetDirectoryName(_realesrganPath);
        if (string.IsNullOrEmpty(dir)) return null; // on PATH — let the tool find its own ./models
        var models = Path.Combine(dir, "models");
        return Directory.Exists(models) ? models : null;
    }

    // === Process + probe helpers ===

    private async Task<(string FrameRate, long EstimatedFrames)> ProbeAsync(string input, CancellationToken ct)
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
        return (rate, frames);
    }

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

    /// <summary>Append a line to the job's rolling log so the Queue tab's log viewer and the on-disk
    /// session log capture what this processor is doing (its ffmpeg/realesrgan output otherwise
    /// went nowhere, making failures invisible outside the raw exit code).</summary>
    private void LogCommand(Job? logJob, string label, string exe, string[] args)
    {
        logJob?.AppendLog($"[{label}] {Path.GetFileName(exe)} {string.Join(' ', args)}");
        _logger.LogInformation("{Processor} [{Step}] {Exe} {Args}", DisplayName, label, Path.GetFileName(exe), string.Join(' ', args));
    }

    /// <summary>Progress from ffmpeg's <c>-progress</c> stream during frame extraction: the probe's
    /// frame estimate maps <c>frame=N</c> onto the extract step's slice of the bar (1–5%).</summary>
    internal static void ReportExtractProgress(string line, long estimatedFrames, IProgress<JobProgress> progress, Stopwatch sw,
        double pctStart = 1, double pctEnd = 5)
    {
        if (estimatedFrames <= 0 || !line.StartsWith("frame=", StringComparison.Ordinal)) return;
        if (!long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)) return;
        var frac = Math.Clamp((double)frame / estimatedFrames, 0, 1);
        Report(progress, pctStart + frac * (pctEnd - pctStart), $"Extracting frames ({frame}/{estimatedFrames})", sw);
    }

    /// <summary>Silence budget per tool run. The model is quiet while it loads onto the GPU and
    /// ffmpeg is quiet while it seeks a large source, but neither for anything like this long.</summary>
    private static readonly TimeSpan StallTimeout = ProcessWatchdog.DefaultStallTimeout;

    /// <summary>
    /// Run one tool to completion. Every line (both pipes) feeds <paramref name="onLine"/>, the job
    /// log, a 12-line tail for the failure message, and the stall watchdog's heartbeat — a tool that
    /// goes silent is killed and reported rather than waited on forever.
    /// </summary>
    private async Task RunAsync(string exe, string[] args, Action<string>? onLine, CancellationToken ct, Job? logJob = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var tail = new System.Collections.Generic.Queue<string>();
        var lastOutputTicks = DateTime.UtcNow.Ticks;
        void Sink(string? line)
        {
            if (line is null) return;
            Volatile.Write(ref lastOutputTicks, DateTime.UtcNow.Ticks);
            onLine?.Invoke(line);
            logJob?.AppendLog(line);
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 12) tail.Dequeue();
            }
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
            await ProcessWatchdog.WaitForExitOrStallAsync(p, () => Volatile.Read(ref lastOutputTicks), StallTimeout,
                $"{DisplayName} ({Path.GetFileName(exe)})", _logger, ct);
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
        => progress.Report(new JobProgress
        {
            Percentage = Math.Clamp(percent, 0, 100),
            Elapsed = sw.Elapsed,
            StatusLine = status,
        });

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
            ? n / d
            : (double.TryParse(frac, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 25.0);
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)%")]
    private static partial Regex PercentRegex();
}
