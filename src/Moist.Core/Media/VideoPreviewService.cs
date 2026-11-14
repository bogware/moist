using System.Diagnostics;
using System.Globalization;

namespace Moist.Core.Media;

/// <summary>
/// Extracts video frames using FFmpeg for preview display.
/// </summary>
public sealed class VideoPreviewService
{
    private readonly string _ffmpegPath;

    public VideoPreviewService(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Extract a single frame at the specified time position.
    /// Returns the path to the extracted PNG file.
    /// </summary>
    public async Task<string?> ExtractFrameAsync(
        string videoPath,
        TimeSpan position,
        int maxWidth = 640,
        CancellationToken ct = default)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"moist_preview_{Guid.NewGuid():N}.png");

        var posStr = position.ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);

        // Use ArgumentList to safely pass paths without shell injection risk
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(posStr);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vframes");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={maxWidth}:-1");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi);
        if (process is null) return null;

        // Drain both pipes concurrently and kill the child on cancellation. FFmpeg dumps
        // its input probe to stderr; on a rich source (many audio/subtitle/attachment
        // streams) that exceeds the OS pipe buffer, and if nothing reads it ffmpeg blocks
        // on the write and WaitForExitAsync never returns.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await Task.WhenAll(stdoutTask, stderrTask);

        return process.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
    }

    /// <summary>
    /// Extract multiple thumbnails for a timeline strip.
    /// </summary>
    public async Task<List<string>> ExtractThumbnailStripAsync(
        string videoPath,
        int count,
        TimeSpan duration,
        int thumbWidth = 160,
        CancellationToken ct = default)
    {
        var results = new List<string>();
        var interval = duration.TotalSeconds / (count + 1);

        for (int i = 1; i <= count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pos = TimeSpan.FromSeconds(interval * i);
            var path = await ExtractFrameAsync(videoPath, pos, thumbWidth, ct);
            if (path is not null)
                results.Add(path);
        }

        return results;
    }

    /// <summary>
    /// Extract a frame with a filter chain applied (for filter preview).
    /// </summary>
    public async Task<string?> ExtractFilteredFrameAsync(
        string videoPath,
        TimeSpan position,
        string filterGraph,
        int maxWidth = 640,
        CancellationToken ct = default)
    {
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"moist_preview_filtered_{Guid.NewGuid():N}.png");

        var posStr = position.ToString(@"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(posStr);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vframes");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add($"{filterGraph},scale={maxWidth}:-1");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi);
        if (process is null) return null;

        // Drain both pipes concurrently and kill the child on cancellation — see
        // ExtractFrameAsync: an undrained stderr dump deadlocks WaitForExitAsync.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await Task.WhenAll(stdoutTask, stderrTask);

        return process.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
    }

    public static void CleanupPreviewFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), "moist_preview_*.png"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
