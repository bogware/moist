using System.Diagnostics;
using Moist.Core.Jobs;
using Moist.Services;

namespace Moist.Cli;

/// <summary>Runs a set of enqueued jobs to completion, rendering progress.</summary>
public sealed class QueueRunner(AppServices services, bool showProgress, bool quiet)
{
    private readonly object _renderLock = new();
    private string _lastLine = string.Empty;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public async Task<bool> RunAsync(IReadOnlyList<string> jobIds, CancellationToken ct)
    {
        var tracked = new HashSet<string>(jobIds);

        void OnProgress(object? _, (Job Job, JobProgress Progress) e)
        {
            if (!showProgress || !tracked.Contains(e.Job.Id)) return;
            Render(e.Job, e.Progress);
        }

        void OnStatus(object? _, Job job)
        {
            if (!tracked.Contains(job.Id)) return;
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                Announce(job);
        }

        services.JobQueue.JobProgressUpdated += OnProgress;
        services.JobQueue.JobStatusChanged += OnStatus;

        try
        {
            var run = services.JobQueue.StartAsync();

            while (!ct.IsCancellationRequested)
            {
                var jobs = jobIds.Select(id => services.JobQueue.Jobs[id]).ToList();
                if (jobs.All(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
                    break;

                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }

            if (ct.IsCancellationRequested)
                foreach (var id in jobIds) services.JobQueue.CancelJob(id);

            services.JobQueue.Stop();
            try { await run.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None); } catch { }

            ClearLine();
            return jobIds.All(id => services.JobQueue.Jobs[id].Status == JobStatus.Completed);
        }
        finally
        {
            services.JobQueue.JobProgressUpdated -= OnProgress;
            services.JobQueue.JobStatusChanged -= OnStatus;
        }
    }

    private void Render(Job job, JobProgress p)
    {
        if (Console.IsOutputRedirected) return;

        var pct = Math.Clamp(p.Percentage, 0, 100);
        var width = 24;
        var filled = (int)Math.Round(pct / 100.0 * width);
        var bar = new string('#', filled) + new string('.', width - filled);

        var detail = p.Fps > 0
            ? $"{p.Fps,6:F1} fps"
            : p.OutputSizeBytes > 0
                ? $"{p.OutputSizeBytes / 1024.0 / 1024.0,6:F1} MB"
                : new string(' ', 10);

        var eta = p.Eta > TimeSpan.Zero && p.Eta < TimeSpan.FromHours(24)
            ? $" eta {Format(p.Eta)}"
            : "";

        var label = job.JobType switch
        {
            "Video Encode" => "video",
            "Audio Encode" => "audio",
            "Mux" => "mux  ",
            _ => job.JobType.ToLowerInvariant(),
        };

        var line = $"  {label} [{bar}] {pct,5:F1}%  {detail}{eta}";

        lock (_renderLock)
        {
            if (line == _lastLine) return;
            var pad = Math.Max(0, _lastLine.Length - line.Length);
            Console.Write("\r" + line + new string(' ', pad));
            _lastLine = line;
        }
    }

    private void ClearLine()
    {
        if (Console.IsOutputRedirected || _lastLine.Length == 0) return;
        lock (_renderLock)
        {
            Console.Write("\r" + new string(' ', _lastLine.Length) + "\r");
            _lastLine = string.Empty;
        }
    }

    private void Announce(Job job)
    {
        if (quiet && job.Status == JobStatus.Completed) return;
        ClearLine();

        var elapsed = job.StartedAt is { } start && job.CompletedAt is { } end && end > start
            ? end - start
            : TimeSpan.Zero;
        switch (job.Status)
        {
            case JobStatus.Completed:
                Console.WriteLine($"  [done]   {job.Name}  ({Format(elapsed)})");
                break;
            case JobStatus.Failed:
                Console.Error.WriteLine($"  [failed] {job.Name}");
                if (job.ErrorMessage is not null)
                    Console.Error.WriteLine($"           {Indent(job.ErrorMessage)}");
                break;
            case JobStatus.Cancelled:
                Console.Error.WriteLine($"  [cancel] {job.Name}");
                break;
        }
    }

    private static string Indent(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.TrimEnd())
                           .Where(l => l.Length > 0)
                           .ToList();
        // The interesting part of an FFmpeg failure is the tail, not the banner.
        var tail = lines.TakeLast(4);
        return string.Join("\n           ", tail);
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m{t.Seconds:D2}s"
         : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:D2}s"
         : $"{t.TotalSeconds:F1}s";
}
