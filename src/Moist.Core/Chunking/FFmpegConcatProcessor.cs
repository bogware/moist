using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Chunking;

/// <summary>
/// Joins encoded chunks with FFmpeg's concat demuxer, copying streams rather than
/// re-encoding, then checks the result actually contains what it should.
///
/// The verification is not optional politeness. Chunked encoding is where a silent
/// off-by-one costs you footage, and FFmpeg will happily exit zero having written a
/// file that is a few frames short.
/// </summary>
public sealed partial class FFmpegConcatProcessor(string ffmpegPath) : CommandLineProcessor
{
    private string? _listFile;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => "ffmpeg-concat";
    public override string DisplayName => "FFmpeg Concat";

    public override bool CanProcess(Job job) => job is ConcatJob;

    protected override string GetExecutablePath(Job job) => ffmpegPath;

    protected override string BuildArguments(Job job)
    {
        _stopwatch.Reset();
        var cj = (ConcatJob)job;

        if (cj.Inputs.Count == 0)
            throw new InvalidOperationException("concat job has no inputs");

        var missing = cj.Inputs.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException($"chunk file(s) missing: {string.Join(", ", missing.Select(Path.GetFileName))}");

        _listFile = WriteListFile(cj);

        // -safe 0 because the list holds absolute paths.
        // -fflags +genpts rebuilds timestamps across the joins; without it the result can
        // carry discontinuities that confuse players and downstream muxing.
        return string.Join(" ", new[]
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-f", "concat", "-safe", "0",
            "-fflags", "+genpts",
            "-i", Quote(_listFile),
            "-c", "copy",
            Quote(cj.OutputFile),
        });
    }

    /// <summary>
    /// The concat demuxer's list format escapes single quotes in a way of its own:
    /// a quote is closed, escaped and reopened.
    /// </summary>
    internal static string WriteListFile(ConcatJob job)
    {
        var listFile = Path.Combine(
            Path.GetDirectoryName(job.OutputFile) ?? Path.GetTempPath(),
            $"moist_concat_{job.Id}.txt");

        var builder = new StringBuilder();
        foreach (var input in job.Inputs)
        {
            var path = Path.GetFullPath(input).Replace('\\', '/');
            builder.Append("file '").Append(path.Replace("'", @"'\''")).Append("'\n");
        }

        File.WriteAllText(listFile, builder.ToString(), new UTF8Encoding(false));
        return listFile;
    }

    protected override async Task OnAfterSuccessAsync(Job job, CancellationToken ct)
    {
        var cj = (ConcatJob)job;

        try
        {
            if (!File.Exists(cj.OutputFile))
                throw new InvalidOperationException("concat produced no output file");

            if (cj.Verify && cj.ExpectedFrames > 0)
            {
                var actual = await CountFramesAsync(cj.FFprobePath, cj.OutputFile, ct);

                if (actual != cj.ExpectedFrames)
                {
                    throw new InvalidOperationException(
                        $"joined output has {actual} frames but the chunks should total {cj.ExpectedFrames}. " +
                        "The chunk boundaries did not line up; the result would be missing or repeating footage.");
                }

                job.AppendLog($"[verify] {actual} frames, matching the plan");
            }
        }
        finally
        {
            if (_listFile is not null)
            {
                try { File.Delete(_listFile); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Count frames by decoding. Container metadata lies often enough — especially after
    /// a concat — that trusting nb_frames would defeat the purpose of verifying.
    /// </summary>
    public static async Task<long> CountFramesAsync(string ffprobePath, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffprobePath,
            $"-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 \"{file}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {ffprobePath}");

        // stderr is redirected, so it must be drained too: a corrupt chunk makes ffprobe spew
        // decode errors, and once they fill the pipe buffer the wait below never returns.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        _ = await stderrTask;

        return ParseFrameCount(stdout);
    }

    /// <summary>
    /// The first number in ffprobe's csv output, or -1. Not a plain parse of the whole output: an
    /// MPEG-TS lists each stream again under its program, so a broadcast capture prints the count
    /// twice with trailing commas ("4462," blank line "4462,") - which a whole-string parse
    /// rejected, and chunk planning silently fell back to a duration estimate 47 frames too high.
    /// </summary>
    public static long ParseFrameCount(string ffprobeOutput)
    {
        foreach (var line in ffprobeOutput.Split((char)10))
        {
            var token = line.Trim().Trim(',').Trim();
            if (long.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var frames))
                return frames;
        }
        return -1;
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        var sizeMatch = SizeRegex().Match(line);
        var timeMatch = TimeRegex().Match(line);

        if (!sizeMatch.Success || !long.TryParse(sizeMatch.Groups[1].Value, out var sizeKb))
            return null;

        double percentage = 0;
        if (timeMatch.Success &&
            TimeSpan.TryParse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var currentTime) &&
            job is ConcatJob { ExpectedFrames: > 0 } cj)
        {
            var totalSeconds = cj.TotalDurationSeconds;
            if (totalSeconds > 0)
                percentage = Math.Min(99.0, currentTime.TotalSeconds / totalSeconds * 100.0);
        }

        return new JobProgress
        {
            Percentage = percentage,
            OutputSizeBytes = sizeKb * 1024,
            Elapsed = _stopwatch.Elapsed,
            StatusLine = line.Trim(),
        };
    }

    [GeneratedRegex(@"size=\s*(\d+)kB")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"time=(\d{1,2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();
}
