using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moist.Services;

/// <summary>
/// Remembers search results so re-encoding the same file with the same settings doesn't
/// repeat several minutes of probing.
///
/// The key includes the file's size and modification time, so editing the source
/// invalidates the entry without anyone having to remember to clear anything.
/// </summary>
public sealed class QualityCache
{
    public sealed class Entry
    {
        public required string Key { get; set; }
        public required int Crf { get; set; }
        public required double Score { get; set; }
        public required bool TargetMet { get; set; }
        public DateTime StoredAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object FileLock = new();

    public static string GetCachePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "moist");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "quality-cache.json");
    }

    public static string BuildKey(EncodeRequest request, TargetQualityOptions options)
    {
        var identity = new StringBuilder();
        identity.Append(Path.GetFullPath(request.InputFile).ToLowerInvariant()).Append('|');

        try
        {
            var file = new FileInfo(request.InputFile);
            if (file.Exists)
                identity.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|');
        }
        catch
        {
            // An unreadable source just produces a weaker key; it can't corrupt anything.
        }

        identity.Append(request.VideoEncoderId).Append('|')
                .Append(request.Preset).Append('|')
                .Append(request.Tune).Append('|')
                .Append(request.BitDepth).Append('|')
                .Append(request.VideoCustomArgs).Append('|')
                .Append(request.FilterScript ?? "").Append('|')
                .Append(options.Metric).Append('|')
                .Append(options.ResolvedTarget.ToString("F3")).Append('|')
                .Append(options.Samples).Append('|')
                .Append(options.SampleDuration.TotalSeconds.ToString("F1"));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        return Convert.ToHexString(hash)[..24];
    }

    public bool TryGet(string key, out Entry? entry)
    {
        entry = Load().FirstOrDefault(e => e.Key == key);
        return entry is not null;
    }

    public void Store(string key, TargetQualityResult result)
    {
        try
        {
            lock (FileLock)
            {
                var entries = Load();
                entries.RemoveAll(e => e.Key == key);
                entries.Add(new Entry
                {
                    Key = key,
                    Crf = result.Crf,
                    Score = result.PredictedScore,
                    TargetMet = result.TargetMet,
                    StoredAt = DateTime.UtcNow,
                });

                // Keep the newest few hundred; this is a convenience, not an archive.
                var trimmed = entries.OrderByDescending(e => e.StoredAt).Take(500).ToList();

                var path = GetCachePath();
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(trimmed, JsonOptions));
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch
        {
            // Losing the cache costs time, never correctness.
        }
    }

    public void Clear()
    {
        try
        {
            lock (FileLock)
            {
                var path = GetCachePath();
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch { /* best effort */ }
    }

    private static List<Entry> Load()
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
