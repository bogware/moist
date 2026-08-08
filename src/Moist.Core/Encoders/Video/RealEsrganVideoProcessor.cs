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

    /// <summary>
    /// Refuse sources longer than this many frames unless <see cref="VideoJob.AiUpscaleAllowLong"/>
    /// is set — at ~3 fps and multi-MB per upscaled frame, a whole film is hours of work and a
    /// mountain of scratch. ~8 minutes at 25 fps.
    /// </summary>
    public const long DefaultMaxFrames = 12_000;

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
        var (frameRate, estimatedFrames) = await ProbeAsync(vj.InputFile, ct);

        if (!vj.AiUpscaleAllowLong && estimatedFrames > DefaultMaxFrames)
            throw new InvalidOperationException(
                $"This source is ~{estimatedFrames:N0} frames. Real-ESRGAN is frame-by-frame and very " +
                $"slow/disk-heavy, so it's capped at {DefaultMaxFrames:N0} frames (~8 min). Trim the source, " +
                "use the GPU 'superres' filter instead, or pass --ai-upscale-allow-long to override.");

        var work = Path.Combine(_tempDir, $"realesrgan_{vj.Id}");
        var framesIn = Path.Combine(work, "in");
        var framesOut = Path.Combine(work, "out");
        Directory.CreateDirectory(framesIn);
        Directory.CreateDirectory(framesOut);

        try
        {
            // 1) Extract cleaned frames (the job's FilterScript is the pre-clean: de-block, denoise).
            Report(progress, 3, "Extracting frames", stopwatch);
            await RunAsync(_ffmpegPath, BuildExtractArgs(vj, Path.Combine(framesIn, "%06d.png")), null, ct);
            var frameCount = Directory.EnumerateFiles(framesIn, "*.png").Count();
            if (frameCount == 0) throw new InvalidOperationException("Frame extraction produced no frames.");

            // 2) Upscale every frame with Real-ESRGAN. This is the slow part; map its % to 10–85.
            await RunAsync(_realesrganPath, BuildRealesrganArgs(vj, framesIn, framesOut, ModelsDir()),
                line =>
                {
                    var m = PercentRegex().Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pct))
                        Report(progress, 10 + pct / 100.0 * 75, "Upscaling (Real-ESRGAN)", stopwatch);
                }, ct);

            // 3) Encode the upscaled frames at the target height with the job's encoder settings.
            Report(progress, 88, "Encoding", stopwatch);
            await RunAsync(_ffmpegPath, BuildEncodeArgs(vj, Path.Combine(framesOut, "%06d.jpg"), frameRate), null, ct);

            // 4) Verify nothing was lost — a re-encode that silently drops frames desyncs the audio.
            var outFrames = await CountFramesAsync(vj.OutputFile, ct);
            if (outFrames > 0 && Math.Abs(outFrames - frameCount) > 1)
                throw new InvalidOperationException(
                    $"Real-ESRGAN output has {outFrames} frames but {frameCount} were upscaled — refusing a desynced result.");

            Report(progress, 100, "Done", stopwatch);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }
    }

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

    internal static string[] BuildEncodeArgs(VideoJob vj, string framesPattern, string frameRate)
    {
        var s = vj.Settings;
        var height = vj.AiUpscaleHeight > 0 ? vj.AiUpscaleHeight : 1080;

        var args = new List<string>
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-framerate", frameRate,          // the source rate, so playback speed is preserved
            "-i", framesPattern,
            // Scale the AI-upscaled frames to the exact target height (aspect preserved), then a light
            // sharpen + deband finish. CAS is gentle here because the model already added detail.
            "-vf", $"scale=-2:{height}:flags=spline,cas=strength=0.4,deband=range=16:blur=1",
            "-c:v", FinalCodec(vj.EncoderId),
        };

        // Rate control: CRF/quality is the natural fit here; anything else falls back to CRF.
        var q = s.Quality.ToString("0.##", CultureInfo.InvariantCulture);
        args.AddRange(["-crf", q]);
        args.AddRange(["-preset", s.Preset.ToString().ToLowerInvariant()]);

        if (s.ResolvePixelFormat(vj.SourceBitDepth) is { } pixFmt)
            args.AddRange(["-pix_fmt", pixFmt]);

        args.Add("-an");
        args.Add(vj.OutputFile);
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

        using var p = Process.Start(psi)!;
        var outText = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

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
        using var p = Process.Start(psi)!;
        var outText = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return long.TryParse(outText.Trim(), out var n) ? n : 0;
    }

    private static async Task RunAsync(string exe, string[] args, Action<string>? onLine, CancellationToken ct)
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
