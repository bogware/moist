using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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

    public RealEsrganVideoProcessor(string ffmpegPath, string ffprobePath, string realesrganPath, string tempDir)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _realesrganPath = realesrganPath;
        _tempDir = tempDir;
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
        var (frameRate, _) = await ProbeAsync(vj.InputFile, ct);

        // Deterministic work dir keyed on source + settings, so an interrupted run resumes (finished
        // segments are reused) rather than starting the whole slog over.
        var work = Path.Combine(_tempDir, $"realesrgan_{WorkKey(vj)}");
        var framesIn = Path.Combine(work, "in");
        Directory.CreateDirectory(framesIn);

        var success = false;
        try
        {
            // 1) Extract cleaned frames once (FilterScript = the pre-clean). Skipped on resume.
            if (!Directory.EnumerateFiles(framesIn, "*.png").Any())
            {
                Report(progress, 2, "Extracting frames", stopwatch);
                var extractArgs = BuildExtractArgs(vj, Path.Combine(framesIn, "%06d.png"));
                LogCommand(vj, "extract", _ffmpegPath, extractArgs);
                await RunAsync(_ffmpegPath, extractArgs, null, ct, vj);
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
                throw new InvalidOperationException(
                    $"Real-ESRGAN output has {outFrames} frames but {frames.Count} were upscaled — refusing a desynced result.");

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
    private static string WorkKey(VideoJob vj)
    {
        var raw = $"{vj.InputFile}|{vj.AiUpscaleModel}|{vj.AiUpscaleHeight}|{vj.EncoderId}|{vj.Settings.Quality}|{vj.FilterScript}";
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
            if (File.Exists(seg) && await CountFramesAsync(seg, ct) == count) { segments.Add(seg); continue; }

            var winIn = Path.Combine(work, "win");
            if (Directory.Exists(winIn)) Directory.Delete(winIn, recursive: true);
            Directory.CreateDirectory(winIn);
            // Copy (don't move) so framesIn stays intact for a robust resume.
            for (var f = start; f < start + count; f++)
                File.Copy(frames[f], Path.Combine(winIn, Path.GetFileName(frames[f])), overwrite: true);

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
        var args = new List<string> { "-y", "-hide_banner", "-i", vj.InputFile };
        // Pre-clean before the AI upscale — an AI model sharpens block noise as happily as detail.
        if (!string.IsNullOrWhiteSpace(vj.FilterScript))
            args.AddRange(["-vf", vj.FilterScript]);
        // Passthrough: exactly one PNG per source frame, so the count can be verified later.
        args.AddRange(["-vsync", "0", framesPattern]);
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

    internal static string[] BuildEncodeArgs(VideoJob vj, string framesPattern, string frameRate, string outFile, long startNumber = 1)
    {
        var s = vj.Settings;
        var height = vj.AiUpscaleHeight > 0 ? vj.AiUpscaleHeight : 1080;

        var args = new List<string>
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-framerate", frameRate,          // the source rate, so playback speed is preserved
            "-start_number", startNumber.ToString(CultureInfo.InvariantCulture),
            "-i", framesPattern,
            // Scale the AI-upscaled frames to the exact target height (aspect preserved), then a light
            // sharpen + deband finish. CAS is gentle here because the model already added detail.
            "-vf", $"scale=-2:{height}:flags=spline,cas=strength=0.4,deband=range=16:blur=1",
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

        if (s.ResolvePixelFormat(vj.SourceBitDepth) is { } pixFmt)
            args.AddRange(["-pix_fmt", pixFmt]);

        args.Add("-an");
        // Write to the caller's target — a per-window segment in the windowed path, or the final
        // output for a whole-clip encode. Using vj.OutputFile unconditionally made every window
        // overwrite the final file and left segments/ empty, so the concat step failed at ~99%.
        args.Add(outFile);
        return [.. args];
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
    private static void LogCommand(Job? logJob, string label, string exe, string[] args)
        => logJob?.AppendLog($"[{label}] {Path.GetFileName(exe)} {string.Join(' ', args)}");

    private static async Task RunAsync(string exe, string[] args, Action<string>? onLine, CancellationToken ct, Job? logJob = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var tail = new System.Collections.Generic.Queue<string>();
        void Sink(string? line)
        {
            if (line is null) return;
            onLine?.Invoke(line);
            logJob?.AppendLog(line);
            tail.Enqueue(line);
            while (tail.Count > 12) tail.Dequeue();
        }
        p.OutputDataReceived += (_, e) => Sink(e.Data);
        p.ErrorDataReceived += (_, e) => Sink(e.Data);

        if (!p.Start())
            throw new InvalidOperationException($"Failed to start {Path.GetFileName(exe)}.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!p.HasExited) p.Kill(entireProcessTree: true); throw; }

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} exited with code {p.ExitCode}.\n{string.Join("\n", tail)}");
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
