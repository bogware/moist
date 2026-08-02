using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Jobs;

namespace Moist.Services;

/// <summary>What happened to one stored encode when we tried to bring it back.</summary>
public sealed class RestoredEncode
{
    public required PersistedEncode Entry { get; init; }
    public required bool Restored { get; init; }

    /// <summary>Jobs put back on the queue, when it worked.</summary>
    public IReadOnlyList<Job> Jobs { get; init; } = [];

    /// <summary>Chunks recovered from the interrupted run, for chunked encodes.</summary>
    public int ReusedChunks { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// Puts encodes from a previous session back on the queue.
///
/// Restored jobs are queued, not started. Coming back to a machine that immediately
/// started churning through work you didn't ask for would be worse than losing the
/// queue, so starting stays a deliberate act (or the auto-start setting).
/// </summary>
public sealed class EncodeQueueRestorer(AppServices services, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<EncodeQueueRestorer>() ?? NullLogger<EncodeQueueRestorer>.Instance;

    public async Task<IReadOnlyList<RestoredEncode>> RestoreAsync(CancellationToken ct = default)
    {
        var pending = services.QueueStore.LoadPending();
        if (pending.Count == 0) return [];

        _logger.LogInformation("Restoring {Count} queued encode(s) from the previous session", pending.Count);

        var results = new List<RestoredEncode>(pending.Count);

        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RestoreOneAsync(entry, ct));
        }

        return results;
    }

    private async Task<RestoredEncode> RestoreOneAsync(PersistedEncode entry, CancellationToken ct)
    {
        try
        {
            // The output may have been produced by the interrupted run before it died.
            if (File.Exists(entry.Request.OutputFile) && new FileInfo(entry.Request.OutputFile).Length > 0)
            {
                services.QueueStore.Remove(entry.Id);
                return new RestoredEncode
                {
                    Entry = entry,
                    Restored = false,
                    Reason = "already finished before the interruption",
                };
            }

            var info = await services.MediaAnalyzer.AnalyzeAsync(entry.Request.InputFile, ct);

            ChunkedEncodePreparation? chunks = null;
            if (entry.Chunking is { } chunking)
            {
                // Re-planning with the stored settings lands on the same boundaries and
                // the same chunk directory, so everything the previous run finished is
                // picked straight back up.
                var coordinator = new ChunkedEncode(services, loggerFactory);
                var plan = await coordinator.PlanAsync(entry.Request, info, chunking, ct);

                if (plan is not null)
                    chunks = coordinator.Prepare(entry.Request, plan, chunking, entry.PerChunkQuality);
            }

            var plan2 = EncodeJobBuilder.Build(
                entry.Request, info, chunks,
                verifyChunks: entry.Chunking?.Verify ?? true,
                ffprobePath: services.Settings.FFprobePath);

            var jobs = plan2.AllJobs.ToList();
            foreach (var job in jobs) services.JobQueue.Enqueue(job);

            // Track the rebuilt final job so the entry clears when this run finishes.
            services.QueueStore.Remove(entry.Id);
            services.QueueStore.Record(
                entry.Request, plan2.FinalJob.Id, entry.Chunking, entry.PerChunkQuality);

            _logger.LogInformation("Restored {Output} as {Jobs} job(s), reusing {Reused} chunk(s)",
                Path.GetFileName(entry.Request.OutputFile), jobs.Count, chunks?.AlreadyDone.Count ?? 0);

            return new RestoredEncode
            {
                Entry = entry,
                Restored = true,
                Jobs = jobs,
                ReusedChunks = chunks?.AlreadyDone.Count ?? 0,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore {Output}", entry.Request.OutputFile);
            services.QueueStore.Remove(entry.Id);

            return new RestoredEncode
            {
                Entry = entry,
                Restored = false,
                Reason = ex.Message,
            };
        }
    }
}
