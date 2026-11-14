using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Media;

namespace Moist.Services;

public enum LibraryIssueKind
{
    /// <summary>FFprobe could not read the file at all.</summary>
    Unreadable,

    /// <summary>Readable, but there is no video stream in it.</summary>
    NoVideoStream,

    /// <summary>Readable, but silent. Often intentional, so this is informational.</summary>
    NoAudioStream,

    /// <summary>The container reports no duration, which usually means an interrupted write.</summary>
    ZeroDuration,

    /// <summary>Decoding produced errors — the file is damaged. Only found by a deep scan.</summary>
    DecodeErrors,

    /// <summary>Decoded fewer frames than the container claims. Truncated.</summary>
    Truncated,
}

public sealed record LibraryIssue(string File, LibraryIssueKind Kind, string Detail)
{
    /// <summary>
    /// Whether this is damage rather than an observation. A silent file is worth
    /// mentioning; it is not a broken file.
    /// </summary>
    public bool IsFault => Kind is not LibraryIssueKind.NoAudioStream;
}

public sealed record LibraryReport
{
    public required IReadOnlyList<LibraryIssue> Issues { get; init; }
    public int FilesScanned { get; init; }
    public int FilesHealthy { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool DeepScan { get; init; }

    public IEnumerable<LibraryIssue> Faults => Issues.Where(i => i.IsFault);
}

/// <summary>
/// Checks a library for files that are damaged or unplayable.
///
/// There are two depths, and the difference matters. A quick scan reads the container
/// header, which is enough to catch a file that was never finished. It is *not* enough to
/// catch damage: a file with a corrupt middle has a perfectly good header and reports a
/// plausible duration. Only decoding it finds that, which is what the deep scan does —
/// at the cost of reading every byte.
/// </summary>
public sealed class LibraryHealthChecker(
    FFprobeMediaAnalyzer analyzer,
    string ffmpegPath = "ffmpeg",
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<LibraryHealthChecker>() ?? NullLogger<LibraryHealthChecker>.Instance;

    public static readonly IReadOnlyList<string> DefaultExtensions =
        [".mkv", ".mp4", ".mov", ".avi", ".ts", ".m2ts", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".m4v"];

    public async Task<LibraryReport> ScanAsync(
        string root,
        bool recursive = true,
        bool deep = false,
        IReadOnlyList<string>? extensions = null,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"library not found: {root}");

        var allowed = (extensions ?? DefaultExtensions)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = Directory
            .EnumerateFiles(root, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => allowed.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var started = Stopwatch.StartNew();
        var issues = new List<LibraryIssue>();
        var healthy = 0;
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, files.Count, file));

            var found = await CheckAsync(file, deep, ct);
            issues.AddRange(found);
            if (!found.Any(i => i.IsFault)) healthy++;

            done++;
        }

        progress?.Report((done, files.Count, ""));

        return new LibraryReport
        {
            Issues = issues,
            FilesScanned = files.Count,
            FilesHealthy = healthy,
            Elapsed = started.Elapsed,
            DeepScan = deep,
        };
    }

    public async Task<IReadOnlyList<LibraryIssue>> CheckAsync(string file, bool deep, CancellationToken ct = default)
    {
        var issues = new List<LibraryIssue>();

        MediaFileInfo info;
        try
        {
            info = await analyzer.AnalyzeAsync(file, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Nothing else can be said about a file that won't open.
            return [new LibraryIssue(file, LibraryIssueKind.Unreadable, Summarize(ex.Message))];
        }

        if (info.Video is null)
            issues.Add(new LibraryIssue(file, LibraryIssueKind.NoVideoStream, "no video stream"));

        if (info.AudioStreams.Count == 0)
            issues.Add(new LibraryIssue(file, LibraryIssueKind.NoAudioStream, "no audio stream"));

        if (info.Duration <= TimeSpan.Zero)
            issues.Add(new LibraryIssue(file, LibraryIssueKind.ZeroDuration,
                "the container reports no duration, which usually means the write was interrupted"));

        if (!deep) return issues;

        var (decodeErrors, framesDecoded) = await DecodeAsync(file, ct);

        if (decodeErrors is { Length: > 0 })
            issues.Add(new LibraryIssue(file, LibraryIssueKind.DecodeErrors, Summarize(decodeErrors)));

        // A container's frame count is a claim, not a measurement. Trusting it is how a
        // truncated file passes as healthy — but only trust the *heuristic* when the claim
        // is itself a real measurement (nb_frames). For VFR/estimate content TotalFrames is
        // duration*r_frame_rate, which overshoots the true count and would flag clean files.
        var claimed = info.Video?.TotalFrames ?? 0;
        if (info.Video?.FrameCountIsExact == true && claimed > 0 && framesDecoded > 0 &&
            framesDecoded < claimed * 0.995)
            issues.Add(new LibraryIssue(file, LibraryIssueKind.Truncated,
                $"decoded {framesDecoded:N0} frames but the container claims {claimed:N0}"));

        return issues;
    }

    /// <summary>
    /// Decode everything and discard it, reporting what FFmpeg complained about.
    /// </summary>
    private async Task<(string? Errors, long Frames)> DecodeAsync(string file, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-v error -xerror -i \"{file}\" -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Health: could not run FFmpeg for {File} — {Error}", file, ex.Message);
            return (null, 0);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        var errors = (await stderr).Trim();
        await stdout;

        // -v error keeps stderr empty for a clean file, so anything here is a real problem.
        return (string.IsNullOrWhiteSpace(errors) ? null : errors, await CountFramesAsync(file, ct));
    }

    private async Task<long> CountFramesAsync(string file, CancellationToken ct)
    {
        // Use the ffprobe the checker was already handed via the analyzer, which knows the
        // configured location. Deriving it from the ffmpeg directory fails silently when the
        // user has ffprobe configured somewhere else, skipping the truncation check entirely.
        var probe = analyzer.FfprobePath;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = probe,
                Arguments = $"-v error -select_streams v:0 -count_frames " +
                            $"-show_entries stream=nb_read_frames -of csv=p=0 \"{file}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        try
        {
            process.Start();

            // Both pipes have to be drained. Reading only stdout deadlocks the moment the
            // child fills the stderr buffer — which is exactly what a damaged file makes
            // ffprobe do, so the hang only ever appeared on the files this exists to find.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            await stderr;

            var output = await stdout;
            // Shared parser: an MPEG-TS prints "N," twice (once per program) and the old first-line
            // parse choked on the trailing comma, reporting 0 frames for every broadcast capture.
            return Math.Max(0, Moist.Core.Chunking.FFmpegConcatProcessor.ParseFrameCount(output));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        catch (Exception ex)
        {
            // Surface the failure: returning 0 silently skips the truncation check, so a
            // truncated file would be reported healthy with no trace of why.
            _logger.LogWarning(
                "Health: frame count failed for {File} — truncation check skipped: {Error}",
                file, ex.Message);
            return 0;
        }
    }

    /// <summary>Keep reports readable — FFmpeg repeats the same complaint per frame.</summary>
    private static string Summarize(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .Take(3)
            .ToList();

        var total = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var summary = string.Join(" | ", lines);

        return total > lines.Count ? $"{summary} (+{total - lines.Count} more)" : summary;
    }
}
