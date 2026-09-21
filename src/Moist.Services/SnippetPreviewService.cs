using Moist.Core.Jobs;
using Moist.Core.Media;

namespace Moist.Services;

/// <summary>The outcome of rendering a preview snippet.</summary>
/// <param name="SnippetPath">Path to the rendered video-only snippet (in the temp dir).</param>
/// <param name="DurationSeconds">Actual snippet duration, for the scrub range.</param>
public sealed record SnippetPreviewResult(string SnippetPath, double DurationSeconds);

/// <summary>
/// Renders a short snippet of an encode <em>through the real pipeline</em> — the same encoder,
/// settings, filter chain, bit depth and AI/RIFE processors the full encode would use — so a
/// preview reflects exactly what would be output, not just the filter chain.
///
/// It builds one trimmed, video-only <see cref="VideoJob"/> via
/// <see cref="EncodeJobBuilder.BuildPreviewVideoJob"/> and runs it through the processor the live
/// queue would pick (<see cref="JobQueue.ResolveProcessor"/>), without touching the user's queue.
/// Because it uses the real encoder, it is as slow-per-second as the encode itself — hence a short
/// default span and an explicit "generate" action rather than continuous re-rendering.
/// </summary>
public sealed class SnippetPreviewService(AppServices services)
{
    /// <summary>Default snippet length. Short, because it is encoded at the real settings.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Render a snippet starting at <paramref name="start"/>. The snippet is clamped to the
    /// source duration. Progress is reported 0-100. Throws only on a genuine failure (no
    /// processor, encoder error); callers show the message.
    /// </summary>
    public async Task<SnippetPreviewResult> RenderAsync(
        EncodeRequest request,
        MediaFileInfo? info,
        TimeSpan start,
        TimeSpan? duration = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var dur = duration ?? DefaultDuration;
        if (info is not null && info.Duration > TimeSpan.Zero)
        {
            if (start >= info.Duration) start = TimeSpan.Zero;
            if (start + dur > info.Duration) dur = info.Duration - start;
        }
        if (dur <= TimeSpan.Zero) dur = SnippetPreviewService.DefaultDuration;

        var previewDir = Path.Combine(services.Settings.TempDirectory, "preview");
        Directory.CreateDirectory(previewDir);

        // A fresh name each render so an open player holding the last file can't block the write.
        var snippet = Path.Combine(previewDir, $"preview_{Guid.NewGuid():N}.mkv");

        var job = EncodeJobBuilder.BuildPreviewVideoJob(request, info, start, dur, snippet);

        var processor = services.JobQueue.ResolveProcessor(job)
            ?? throw new InvalidOperationException(
                $"No processor is available for encoder '{request.VideoEncoderId}'. Is the tool installed?");

        var adapter = new Progress<JobProgress>(p => progress?.Report(p.Percentage));
        await processor.ProcessAsync(job, adapter, ct);

        if (job.Status == JobStatus.Failed)
            throw new InvalidOperationException(job.ErrorMessage ?? "Preview render failed.");
        if (!File.Exists(snippet))
            throw new InvalidOperationException("Preview render produced no output.");

        CleanupOldSnippets(previewDir, keep: snippet);
        return new SnippetPreviewResult(snippet, dur.TotalSeconds);
    }

    /// <summary>Best-effort removal of previous snippets so the preview dir doesn't accumulate.</summary>
    private static void CleanupOldSnippets(string dir, string keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "preview_*.mkv"))
            {
                if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { /* a player may still hold it; leave it */ }
            }
        }
        catch { /* best effort */ }
    }
}
