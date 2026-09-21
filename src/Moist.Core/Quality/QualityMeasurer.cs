using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Quality;

/// <summary>Options for a single measurement.</summary>
public sealed class MeasureOptions
{
    /// <summary>0 lets FFmpeg decide.</summary>
    public int Threads { get; init; }

    /// <summary>
    /// Seek into the reference before comparing. Used when the distorted file is a short
    /// sample taken from partway through the source, as target-quality search does.
    /// </summary>
    public TimeSpan? ReferenceStart { get; init; }

    /// <summary>Limit how much of the reference is read. Pairs with <see cref="ReferenceStart"/>.</summary>
    public TimeSpan? ReferenceDuration { get; init; }

    /// <summary>
    /// Scale the distorted video up to the reference resolution before comparing.
    /// Without this a downscaled encode can't be measured at all.
    /// </summary>
    public bool ScaleToReference { get; init; } = true;

    /// <summary>Reference dimensions, when already known, to skip a probe.</summary>
    public (int Width, int Height)? ReferenceSize { get; init; }
}

/// <summary>
/// Measures encoded video against its source using FFmpeg's built-in metric filters.
///
/// No extra tooling is needed: libvmaf, xpsnr, ssim and psnr all ship in standard
/// FFmpeg builds.
/// </summary>
public sealed partial class QualityMeasurer(string ffmpegPath, string ffprobePath, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<QualityMeasurer>() ?? NullLogger<QualityMeasurer>.Instance;

    public async Task<QualityResult> MeasureAsync(
        string referenceFile,
        string distortedFile,
        QualityMetric metric,
        MeasureOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new MeasureOptions();

        if (!File.Exists(referenceFile)) throw new FileNotFoundException("reference not found", referenceFile);
        if (!File.Exists(distortedFile)) throw new FileNotFoundException("distorted file not found", distortedFile);

        // Metric filters run in a scratch directory so libvmaf's JSON log can be written
        // with a bare filename — Windows paths need painful escaping inside filter args.
        var workDir = Path.Combine(Path.GetTempPath(), "moist-quality-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        try
        {
            var size = options.ScaleToReference
                ? options.ReferenceSize ?? await ProbeSizeAsync(referenceFile, ct)
                : null;

            var args = BuildArguments(referenceFile, distortedFile, metric, options, size);
            _logger.LogDebug("Measuring {Metric}: {Args}", metric, args);

            var stopwatch = Stopwatch.StartNew();
            var stderr = await RunAsync(args, workDir, ct);
            stopwatch.Stop();

            var result = metric switch
            {
                QualityMetric.Vmaf => ParseVmaf(stderr, Path.Combine(workDir, VmafLogName)),
                QualityMetric.Xpsnr => ParseXpsnr(stderr),
                QualityMetric.Ssim => ParseSsim(stderr),
                QualityMetric.Psnr => ParsePsnr(stderr),
                _ => throw new NotSupportedException($"metric {metric} is not supported"),
            };

            return new QualityResult
            {
                Metric = metric,
                Score = result.Score,
                Minimum = result.Minimum,
                Maximum = result.Maximum,
                HarmonicMean = result.HarmonicMean,
                Components = result.Components,
                FramesMeasured = result.Frames,
                Elapsed = stopwatch.Elapsed,
            };
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string VmafLogName = "vmaf.json";

    /// <summary>
    /// Stop as soon as either input runs out, and don't hold the last frame.
    ///
    /// This matters enormously. Comparing a 5-second sample against a 40-second source
    /// without it keeps going for the remaining 35 seconds, scoring a frozen frame
    /// against moving video — which dragged one measured clip from 98 down to 31.
    /// Every metric filter here is built on FFmpeg's framesync and needs it.
    /// </summary>
    private const string FrameSync = "shortest=1:repeatlast=0";

    internal string BuildArguments(
        string referenceFile, string distortedFile, QualityMetric metric,
        MeasureOptions options, (int Width, int Height)? referenceSize)
    {
        var args = new List<string> { "-hide_banner", "-nostdin" };

        // Distorted first so it becomes input 0, matching the filter wiring below.
        args.AddRange(["-i", Quote(distortedFile)]);

        // Seeking before -i is the fast path and stays frame-aligned with an encode
        // that used the same seek.
        if (options.ReferenceStart is { } start)
            args.AddRange(["-ss", start.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", Quote(referenceFile)]);
        if (options.ReferenceDuration is { } duration)
            args.AddRange(["-t", duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);

        // settb=AVTB normalises the timebases; without it FFmpeg warns that results may
        // be wrong when the two files were produced by different muxers.
        var distortedChain = "settb=AVTB,setpts=PTS-STARTPTS";
        if (referenceSize is { } size)
            distortedChain = $"scale={size.Width}:{size.Height}:flags=bicubic,{distortedChain}";

        var filter = metric switch
        {
            QualityMetric.Vmaf => BuildVmafFilter(options, referenceSize),
            QualityMetric.Xpsnr => $"xpsnr={FrameSync}",
            QualityMetric.Ssim => $"ssim={FrameSync}",
            QualityMetric.Psnr => $"psnr={FrameSync}",
            _ => throw new NotSupportedException($"metric {metric} is not supported"),
        };

        args.AddRange([
            "-lavfi",
            Quote($"[0:v]{distortedChain}[dist];[1:v]settb=AVTB,setpts=PTS-STARTPTS[ref];[dist][ref]{filter}"),
        ]);

        args.AddRange(["-f", "null", "-"]);
        return string.Join(" ", args);
    }

    private static string BuildVmafFilter(MeasureOptions options, (int Width, int Height)? referenceSize)
    {
        var parts = new List<string>();

        if (options.Threads > 0)
            parts.Add($"n_threads={options.Threads}");

        // The default model is trained for 1080p viewing distance; 4K content is scored
        // more accurately by the 4k model.
        if (referenceSize is { } size && size.Width >= 2560)
            parts.Add("model=version=vmaf_4k_v0.6.1");

        parts.Add("log_fmt=json");
        parts.Add($"log_path={VmafLogName}");
        parts.Add(FrameSync);

        return "libvmaf=" + string.Join(":", parts);
    }

    private async Task<string> RunAsync(string args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpegPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {ffmpegPath}");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stderr = await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0)
        {
            var tail = string.Join("\n", stderr.Split('\n').TakeLast(5).Select(l => l.Trim()));
            throw new InvalidOperationException($"quality measurement failed (exit {process.ExitCode}):\n{tail}");
        }

        return stderr;
    }

    private async Task<(int Width, int Height)?> ProbeSizeAsync(string file, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobePath,
                $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 {Quote(file)}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            // Drain both pipes before waiting for exit: ffprobe on a malformed source can
            // write enough to stderr to fill the OS pipe buffer, which would block it (and
            // therefore its stdout, and this await) forever if stderr is left unread.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            await stderrTask;

            var parts = stdout.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                return (w, h);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not probe reference size for {File}", file);
        }

        return null;
    }

    // ---------------- parsing ----------------

    internal readonly record struct Parsed(
        double Score, double? Minimum, double? Maximum, double? HarmonicMean,
        IReadOnlyDictionary<string, double> Components, long Frames);

    internal static Parsed ParseVmaf(string stderr, string jsonLogPath)
    {
        // Prefer the JSON log: it carries pooled statistics the console line doesn't.
        if (File.Exists(jsonLogPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonLogPath));
                var root = document.RootElement;

                if (root.TryGetProperty("pooled_metrics", out var pooled) &&
                    pooled.TryGetProperty("vmaf", out var vmaf))
                {
                    var frames = root.TryGetProperty("frames", out var f) ? f.GetArrayLength() : 0;
                    return new Parsed(
                        Score: ReadDouble(vmaf, "mean"),
                        Minimum: TryReadDouble(vmaf, "min"),
                        Maximum: TryReadDouble(vmaf, "max"),
                        HarmonicMean: TryReadDouble(vmaf, "harmonic_mean"),
                        Components: new Dictionary<string, double>(),
                        Frames: frames);
                }
            }
            catch
            {
                // Fall through to the console line.
            }
        }

        var match = VmafScoreRegex().Match(stderr);
        if (!match.Success)
            throw new InvalidOperationException("could not find a VMAF score in FFmpeg's output");

        return new Parsed(ParseDouble(match.Groups[1].Value), null, null, null,
            new Dictionary<string, double>(), 0);
    }

    internal static Parsed ParseXpsnr(string stderr)
    {
        var match = XpsnrRegex().Match(stderr);
        if (!match.Success)
            throw new InvalidOperationException("could not find an XPSNR score in FFmpeg's output");

        var y = ParseDouble(match.Groups[1].Value);
        var u = ParseDouble(match.Groups[2].Value);
        var v = ParseDouble(match.Groups[3].Value);

        // Luma dominates perception, so the headline number is the weighted average
        // FFmpeg itself reports for the frame: 4:1:1 across the planes.
        var score = (4 * y + u + v) / 6.0;

        return new Parsed(score, Math.Min(y, Math.Min(u, v)), null, null,
            new Dictionary<string, double> { ["y"] = y, ["u"] = u, ["v"] = v }, 0);
    }

    internal static Parsed ParseSsim(string stderr)
    {
        var match = SsimRegex().Match(stderr);
        if (!match.Success)
            throw new InvalidOperationException("could not find an SSIM score in FFmpeg's output");

        return new Parsed(
            Score: ParseDouble(match.Groups[4].Value),
            Minimum: null, Maximum: null, HarmonicMean: null,
            Components: new Dictionary<string, double>
            {
                ["y"] = ParseDouble(match.Groups[1].Value),
                ["u"] = ParseDouble(match.Groups[2].Value),
                ["v"] = ParseDouble(match.Groups[3].Value),
            },
            Frames: 0);
    }

    internal static Parsed ParsePsnr(string stderr)
    {
        var match = PsnrRegex().Match(stderr);
        if (!match.Success)
            throw new InvalidOperationException("could not find a PSNR score in FFmpeg's output");

        return new Parsed(
            Score: ParseDouble(match.Groups[4].Value),
            Minimum: ParseDouble(match.Groups[5].Value),
            Maximum: ParseDouble(match.Groups[6].Value),
            HarmonicMean: null,
            Components: new Dictionary<string, double>
            {
                ["y"] = ParseDouble(match.Groups[1].Value),
                ["u"] = ParseDouble(match.Groups[2].Value),
                ["v"] = ParseDouble(match.Groups[3].Value),
            },
            Frames: 0);
    }

    private static double ParseDouble(string raw)
    {
        // A frame that encoded losslessly makes FFmpeg's psnr/xpsnr/ssim filters print the
        // literal token "inf" (and "nan" for a degenerate plane) instead of a number. Map
        // those to the corresponding doubles rather than throwing on the parse.
        if (string.Equals(raw, "inf", StringComparison.OrdinalIgnoreCase))
            return double.PositiveInfinity;
        if (string.Equals(raw, "nan", StringComparison.OrdinalIgnoreCase))
            return double.NaN;
        return double.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(JsonElement element, string name)
        => element.GetProperty(name).GetDouble();

    private static double? TryReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string Quote(string s) => $"\"{s}\"";

    [GeneratedRegex(@"VMAF score:\s*([\d.]+)")]
    private static partial Regex VmafScoreRegex();

    [GeneratedRegex(@"XPSNR\s+y:\s*(inf|nan|[\d.]+)\s+u:\s*(inf|nan|[\d.]+)\s+v:\s*(inf|nan|[\d.]+)")]
    private static partial Regex XpsnrRegex();

    [GeneratedRegex(@"SSIM Y:(inf|nan|[\d.]+).*?U:(inf|nan|[\d.]+).*?V:(inf|nan|[\d.]+).*?All:(inf|nan|[\d.]+)")]
    private static partial Regex SsimRegex();

    [GeneratedRegex(@"PSNR y:(inf|nan|[\d.]+) u:(inf|nan|[\d.]+) v:(inf|nan|[\d.]+) average:(inf|nan|[\d.]+) min:(inf|nan|[\d.]+) max:(inf|nan|[\d.]+)")]
    private static partial Regex PsnrRegex();
}
