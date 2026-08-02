using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Chunking;

/// <summary>
/// Finds scene changes with FFmpeg's <c>scdet</c> filter.
///
/// This costs a full decode of the source, which is the price of chunk boundaries that
/// land where the encoder wanted a keyframe anyway. <see cref="ChunkMode.Fixed"/> skips
/// it entirely when that trade isn't worth it.
/// </summary>
public sealed partial class SceneDetector(string ffmpegPath, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<SceneDetector>() ?? NullLogger<SceneDetector>.Instance;

    /// <summary>Progress callback: fraction of the source analysed, 0-1.</summary>
    public event Action<double>? Progress;

    /// <summary>
    /// Frame numbers where a new scene begins.
    /// Returns an empty list if detection fails — the caller can still chunk at fixed
    /// intervals, which is worse but not broken.
    /// </summary>
    public async Task<IReadOnlyList<long>> DetectAsync(
        string inputFile, double framerate, double threshold = 10.0,
        TimeSpan? duration = null, CancellationToken ct = default)
    {
        if (framerate <= 0) return [];

        var args = $"-hide_banner -nostdin -i \"{inputFile}\" " +
                   $"-vf scdet=threshold={threshold.ToString("F2", CultureInfo.InvariantCulture)} " +
                   $"-an -sn -f null -";

        _logger.LogDebug("Detecting scenes: {Args}", args);

        var cuts = new List<long>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return [];

            var totalSeconds = duration?.TotalSeconds ?? 0;

            // scdet reports on stderr, one line per detected change.
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;

                var match = SceneTimeRegex().Match(e.Data);
                if (match.Success &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    var frame = (long)Math.Round(seconds * framerate);
                    lock (cuts) cuts.Add(frame);
                    return;
                }

                if (totalSeconds > 0)
                {
                    var progress = ProgressTimeRegex().Match(e.Data);
                    if (progress.Success &&
                        TimeSpan.TryParse(progress.Groups[1].Value, CultureInfo.InvariantCulture, out var at))
                    {
                        Progress?.Invoke(Math.Clamp(at.TotalSeconds / totalSeconds, 0, 1));
                    }
                }
            };

            process.BeginErrorReadLine();
            _ = process.StandardOutput.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("Scene detection exited with code {Code}; falling back to fixed chunks",
                    process.ExitCode);
                return [];
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scene detection failed; falling back to fixed chunks");
            return [];
        }

        lock (cuts)
        {
            var ordered = cuts.Distinct().OrderBy(f => f).ToList();
            _logger.LogInformation("Found {Count} scene change(s) in {Elapsed:0.0}s",
                ordered.Count, stopwatch.Elapsed.TotalSeconds);
            return ordered;
        }
    }

    // [Parsed_scdet_0 @ 0000…] lavfi.scd.score: 18.156, lavfi.scd.time: 4
    [GeneratedRegex(@"lavfi\.scd\.time:\s*([\d.]+)")]
    private static partial Regex SceneTimeRegex();

    [GeneratedRegex(@"time=(\d{1,2}:\d{2}:\d{2}\.\d+)")]
    private static partial Regex ProgressTimeRegex();
}
