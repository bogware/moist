using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Chunking;

namespace Moist.Services;

/// <summary>An encode that was queued but hadn't finished.</summary>
public sealed record PersistedEncode
{
    public required string Id { get; init; }
    public required EncodeRequest Request { get; init; }
    public DateTime QueuedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Chunking settings, when the encode was chunked. Null otherwise.</summary>
    public ChunkingOptions? Chunking { get; init; }

    /// <summary>Per-scene CRF assignments, so a restored run doesn't repeat the analysis.</summary>
    public Dictionary<int, double>? PerChunkQuality { get; init; }

    public string DisplayName => Path.GetFileName(Request.OutputFile);
}

/// <summary>
/// Remembers queued encodes so they survive a crash or a closed window.
///
/// The previous attempt at this stored a flattened snapshot of each job — id, name,
/// input, output — which is not enough to rebuild anything: the encoder settings weren't
/// there, so a restored job had nothing to run. That's why it never got wired up.
///
/// What is stored instead is the <see cref="EncodeRequest"/>, because
/// <see cref="EncodeJobBuilder.Build"/> turns one deterministically back into the whole
/// job graph. Chunked encodes recover better still: their chunk directory is keyed on
/// the same settings, so a restored run picks up every chunk the previous one finished.
/// </summary>
public sealed class EncodeQueueStore(ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<EncodeQueueStore>() ?? NullLogger<EncodeQueueStore>.Instance;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object FileLock = new();

    /// <summary>Final job id -> stored encode id, for the encodes running right now.</summary>
    private readonly Dictionary<string, string> _liveJobs = new(StringComparer.Ordinal);

    public static string GetQueueFilePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "moist");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "queue.json");
    }

    /// <summary>
    /// Remember an encode that has just been queued. <paramref name="finalJobId"/> is
    /// watched so the entry can be dropped the moment the encode finishes.
    /// </summary>
    public void Record(
        EncodeRequest request,
        string finalJobId,
        ChunkingOptions? chunking = null,
        IReadOnlyDictionary<int, double>? perChunkQuality = null)
    {
        try
        {
            var entry = new PersistedEncode
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Request = request,
                Chunking = chunking,
                PerChunkQuality = perChunkQuality?.ToDictionary(kv => kv.Key, kv => kv.Value),
            };

            lock (FileLock)
            {
                _liveJobs[finalJobId] = entry.Id;
                var all = LoadAll();
                all.Add(entry);
                Write(all);
            }

            _logger.LogDebug("Recorded queued encode {Id} for {Output}", entry.Id, request.OutputFile);
        }
        catch (Exception ex)
        {
            // Losing the ability to resume is never worth failing an encode over.
            _logger.LogWarning(ex, "Could not record queued encode");
        }
    }

    /// <summary>
    /// Called as jobs reach a terminal state. Drops the stored entry once the job that
    /// represents the whole encode is done, whether it succeeded or not — a failed encode
    /// shouldn't come back on every launch.
    /// </summary>
    public void NotifyJobFinished(string jobId)
    {
        string? entryId;
        lock (FileLock)
        {
            if (!_liveJobs.TryGetValue(jobId, out entryId)) return;
            _liveJobs.Remove(jobId);
        }

        Remove(entryId);
    }

    public void Remove(string entryId)
    {
        try
        {
            lock (FileLock)
            {
                var all = LoadAll();
                if (all.RemoveAll(e => e.Id == entryId) > 0) Write(all);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove queued encode {Id}", entryId);
        }
    }

    /// <summary>
    /// Encodes left over from a previous session. Unlike the old implementation this does
    /// not delete the file on read — an entry is dropped when its encode finishes or when
    /// the caller decides it can't be restored, not merely because it was looked at.
    /// </summary>
    public IReadOnlyList<PersistedEncode> LoadPending()
    {
        lock (FileLock)
        {
            var all = LoadAll();

            // Drop anything whose source has since gone away; it can never be restored.
            var usable = all.Where(e => File.Exists(e.Request.InputFile)).ToList();
            if (usable.Count != all.Count)
            {
                _logger.LogInformation("Discarding {Count} queued encode(s) whose source no longer exists",
                    all.Count - usable.Count);
                Write(usable);
            }

            return usable;
        }
    }

    public void Clear()
    {
        try
        {
            lock (FileLock)
            {
                _liveJobs.Clear();
                var path = GetQueueFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the queue file");
        }
    }

    private static List<PersistedEncode> LoadAll()
    {
        try
        {
            var path = GetQueueFilePath();
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<PersistedEncode>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch
        {
            // A corrupt queue file costs a resume, not correctness.
            return [];
        }
    }

    private static void Write(List<PersistedEncode> entries)
    {
        var path = GetQueueFilePath();

        if (entries.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
