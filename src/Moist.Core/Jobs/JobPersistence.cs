using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moist.Core.Jobs;

/// <summary>
/// Lightweight serializable snapshot of a job for persistence across app restarts.
/// Only queued/pending jobs are saved; running jobs are marked as interrupted.
/// </summary>
public sealed class PersistedJob
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string InputFile { get; set; } = string.Empty;
    public string OutputFile { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> DependsOn { get; set; } = [];
}

/// <summary>
/// Saves and loads job queue state to/from disk for crash recovery.
/// </summary>
public static class JobPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string GetQueueFilePath()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "moist");
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "queue.json");
    }

    /// <summary>
    /// Save the current queue state. Only saves jobs that haven't completed.
    /// </summary>
    public static void Save(IReadOnlyDictionary<string, Job> jobs)
    {
        var toSave = jobs.Values
            .Where(j => j.Status is JobStatus.Queued or JobStatus.Pending or JobStatus.Running)
            .Select(j => new PersistedJob
            {
                Id = j.Id,
                Name = j.Name,
                JobType = j.JobType,
                InputFile = j.InputFile,
                OutputFile = j.OutputFile,
                // Running jobs become "interrupted" — mark as failed so user can see
                Status = j.Status == JobStatus.Running ? JobStatus.Failed : j.Status,
                CreatedAt = j.CreatedAt,
                DependsOn = j.DependsOn,
            })
            .ToList();

        if (toSave.Count == 0)
        {
            // No jobs to save — clean up any existing file
            var path = GetQueueFilePath();
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(toSave, JsonOptions);
            File.WriteAllText(GetQueueFilePath(), json);
        }
        catch { /* best effort — don't crash on save failure */ }
    }

    /// <summary>
    /// Load previously persisted jobs. Returns empty list if none found.
    /// </summary>
    public static List<PersistedJob> Load()
    {
        var path = GetQueueFilePath();
        if (!File.Exists(path)) return [];

        try
        {
            var json = File.ReadAllText(path);
            var jobs = JsonSerializer.Deserialize<List<PersistedJob>>(json, JsonOptions);
            // Clean up the file after loading — it's a one-shot recovery
            File.Delete(path);
            return jobs ?? [];
        }
        catch
        {
            return [];
        }
    }
}
