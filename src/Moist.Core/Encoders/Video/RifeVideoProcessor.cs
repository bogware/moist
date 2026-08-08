using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
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
    private const string Model = "rife-v4.6"; // best quality; ships with the tool

    public RifeVideoProcessor(string ffmpegPath, string ffprobePath, string rifePath, string tempDir)
    {
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _rifePath = rifePath;
        _tempDir = tempDir;
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
        var (srcFps, srcFrames) = await ProbeAsync(vj.InputFile, ct);
        var target = TargetFrameCount(srcFrames, srcFps, vj.RifeTargetFps);

        var work = Path.Combine(_tempDir, $"rife_{vj.Id}");
        var framesIn = Path.Combine(work, "in");
        var framesOut = Path.Combine(work, "out");
        Directory.CreateDirectory(framesIn);
        Directory.CreateDirectory(framesOut);

        var success = false;
        try
        {
            // Extract frames (any FilterScript is applied here), interpolate to the target count,
            // then encode at the requested rate. Reuses Real-ESRGAN's extract/codec builders.
            Report(progress, 3, "Extracting frames", sw);
            await RunAsync(_ffmpegPath, RealEsrganVideoProcessor.BuildExtractArgs(vj, Path.Combine(framesIn, "%06d.png")), null, ct);
            if (!Directory.EnumerateFiles(framesIn, "*.png").Any())
                throw new InvalidOperationException("Frame extraction produced no frames.");

            await RunAsync(_rifePath, BuildRifeArgs(framesIn, framesOut, ModelDir(), target),
                line =>
                {
                    var m = PercentRegex().Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var pct))
                        Report(progress, 8 + pct / 100.0 * 80, "Interpolating (RIFE)", sw);
                }, ct);

            Report(progress, 90, "Encoding", sw);
            await RunAsync(_ffmpegPath, BuildEncodeArgs(vj, Path.Combine(framesOut, "%06d.png")), null, ct);
            Report(progress, 100, "Done", sw);
            success = true;
        }
        finally
        {
            if (success) { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
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

    private async Task<(double Fps, long Frames)> ProbeAsync(string input, CancellationToken ct)
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

        var fps = ParseFraction(Match(outText, "r_frame_rate") ?? "25/1");
        long frames = 0;
        if (long.TryParse(Match(outText, "nb_frames"), out var nb) && nb > 0) frames = nb;
        else if (double.TryParse(Match(outText, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) && dur > 0)
            frames = (long)Math.Round(dur * fps);
        return (fps, frames);
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
        var tail = new Queue<string>();
        void Sink(string? line) { if (line is null) return; onLine?.Invoke(line); tail.Enqueue(line); while (tail.Count > 12) tail.Dequeue(); }
        p.OutputDataReceived += (_, e) => Sink(e.Data);
        p.ErrorDataReceived += (_, e) => Sink(e.Data);
        if (!p.Start()) throw new InvalidOperationException($"Failed to start {Path.GetFileName(exe)}.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!p.HasExited) p.Kill(entireProcessTree: true); throw; }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with code {p.ExitCode}.\n{string.Join("\n", tail)}");
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
