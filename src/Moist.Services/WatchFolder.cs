using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Settings;

namespace Moist.Services;

/// <summary>What to do with a source file once its encode has succeeded.</summary>
public enum WatchPostAction
{
    /// <summary>Leave it alone. The record of having encoded it is what stops a repeat.</summary>
    Keep,

    /// <summary>Move it aside, so the watched folder only ever holds work not yet done.</summary>
    Move,

    /// <summary>Delete it. Only ever after the encode succeeded.</summary>
    Delete,
}

public sealed record WatchFolderOptions
{
    /// <summary>Directory to watch.</summary>
    public required string Path { get; init; }

    /// <summary>Where encodes are written. Must not sit inside the watched folder.</summary>
    public string? OutputDirectory { get; init; }

    public bool Recursive { get; init; }

    /// <summary>Extensions to consider, lowercase and dotted.</summary>
    public IReadOnlyList<string> Extensions { get; init; } =
        [".mkv", ".mp4", ".mov", ".avi", ".ts", ".m2ts", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".m4v"];

    /// <summary>How often the folder is re-listed.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a file's size and timestamp must hold still before it is considered
    /// finished being written. A file still uploading is the normal case for a watch
    /// folder, and encoding half of one produces a truncated result with no error.
    /// </summary>
    public TimeSpan StableFor { get; init; } = TimeSpan.FromSeconds(30);

    public WatchPostAction PostAction { get; init; } = WatchPostAction.Keep;

    /// <summary>Destination for <see cref="WatchPostAction.Move"/>.</summary>
    public string? MoveTo { get; init; }

    /// <summary>Encode what is already there at startup, not only what arrives later.</summary>
    public bool IncludeExisting { get; init; } = true;
}

/// <summary>A file the watcher has dealt with, and the state it was in at the time.</summary>
public sealed record WatchedFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public long ModifiedTicks { get; init; }
    public bool Succeeded { get; init; }
    public DateTime HandledUtc { get; init; }
}

public sealed record WatchResult(string Input, string? Output, bool Success, string? Error);

/// <summary>
/// Watches a folder and encodes what lands in it.
///
/// Polling rather than <see cref="FileSystemWatcher"/> on purpose: watch folders are
/// usually network shares or mounted volumes, where change notifications are unreliable or
/// absent, and where a dropped event means a file is silently never encoded. A re-list
/// every few seconds cannot miss anything.
/// </summary>
public sealed class WatchFolderService
{
    private readonly WatchFolderOptions _options;
    private readonly ILogger _logger;
    private readonly string _statePath;
    private readonly Dictionary<string, WatchedFile> _handled;

    /// <summary>Files seen but not yet settled: path to (size, modified, first seen).</summary>
    private readonly Dictionary<string, (long Size, long Ticks, DateTime FirstSeen)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public WatchFolderService(WatchFolderOptions options, ILoggerFactory? loggerFactory = null, string? statePath = null)
    {
        _options = Validate(options);
        _logger = loggerFactory?.CreateLogger<WatchFolderService>() ?? NullLogger<WatchFolderService>.Instance;
        _statePath = statePath ?? Path.Combine(
            Path.GetDirectoryName(MoistSettings.GetSettingsPath())!,
            "watch-" + Hash(_options.Path) + ".json");
        _handled = Load();
    }

    /// <summary>Everything encoded so far, successful or not.</summary>
    public IReadOnlyCollection<WatchedFile> Handled => _handled.Values;

    private static WatchFolderOptions Validate(WatchFolderOptions options)
    {
        if (!Directory.Exists(options.Path))
            throw new DirectoryNotFoundException($"watch folder not found: {options.Path}");

        // Writing encodes back into the folder being watched feeds the watcher its own
        // output: it encodes the encode, then encodes that, until the disk fills.
        if (options.OutputDirectory is { } output && IsInside(output, options.Path))
            throw new ArgumentException(
                "the output directory is inside the watched folder, so every encode would be picked up " +
                "and re-encoded. Choose an output directory outside it.");

        if (options.PostAction == WatchPostAction.Move)
        {
            if (string.IsNullOrWhiteSpace(options.MoveTo))
                throw new ArgumentException("moving handled files needs a destination directory.");
            if (IsInside(options.MoveTo, options.Path) && options.Recursive)
                throw new ArgumentException(
                    "the move destination is inside the watched folder and the watch is recursive, " +
                    "so moved files would be found again.");
        }

        return options;
    }

    private static bool IsInside(string candidate, string parent)
    {
        var a = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        return string.Equals(a, b, comparison)
               || a.StartsWith(b + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Watch until cancelled, handing each settled file to <paramref name="encode"/>.
    /// </summary>
    public async Task RunAsync(
        Func<string, CancellationToken, Task<WatchResult>> encode,
        Action<WatchResult>? onFinished = null,
        CancellationToken ct = default)
    {
        // Anything already present at startup is either claimed as work or written off,
        // depending on IncludeExisting — otherwise "existing" would just mean "arrived
        // while the watcher happened not to be running".
        if (!_options.IncludeExisting)
        {
            foreach (var file in Enumerate())
                Remember(file, succeeded: true, note: "present before the watch started");
            Save();
        }

        while (!ct.IsCancellationRequested)
        {
            foreach (var file in Ready())
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogInformation("Watch: encoding {File}", file);

                WatchResult result;
                try
                {
                    result = await encode(file, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result = new WatchResult(file, null, false, ex.Message);
                }

                Remember(file, result.Success);
                Save();

                if (result.Success) ApplyPostAction(file);
                else _logger.LogError("Watch: {File} failed — {Error}", file, result.Error);

                onFinished?.Invoke(result);
            }

            try { await Task.Delay(_options.ScanInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Files that are new, complete, and not already dealt with.</summary>
    public IReadOnlyList<string> Ready()
    {
        var ready = new List<string>();
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Enumerate())
        {
            seen.Add(file);

            FileInfo info;
            try { info = new FileInfo(file); if (!info.Exists) continue; }
            catch { continue; }

            var size = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;

            // Already done — unless it has changed since, which makes it new work.
            if (_handled.TryGetValue(file, out var previous)
                && previous.Size == size && previous.ModifiedTicks == ticks)
            {
                _pending.Remove(file);
                continue;
            }

            if (size == 0) continue;

            if (!_pending.TryGetValue(file, out var pending) || pending.Size != size || pending.Ticks != ticks)
            {
                // First sighting, or it grew since the last one: restart the clock.
                _pending[file] = (size, ticks, now);
                continue;
            }

            if (now - pending.FirstSeen < _options.StableFor) continue;

            ready.Add(file);
        }

        // Files that vanished before settling shouldn't hold memory forever.
        foreach (var gone in _pending.Keys.Where(p => !seen.Contains(p)).ToList())
            _pending.Remove(gone);

        return ready;
    }

    private IEnumerable<string> Enumerate()
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_options.Path, "*",
                _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            // A share that went away should not kill the watcher; the next scan may find it.
            _logger.LogWarning("Watch: cannot list {Path} — {Error}", _options.Path, ex.Message);
            yield break;
        }

        foreach (var file in files)
        {
            if (_options.Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                yield return file;
        }
    }

    private void ApplyPostAction(string file)
    {
        try
        {
            switch (_options.PostAction)
            {
                case WatchPostAction.Delete:
                    File.Delete(file);
                    _logger.LogInformation("Watch: deleted {File}", file);
                    break;

                case WatchPostAction.Move:
                    var destination = Path.Combine(_options.MoveTo!, Path.GetFileName(file));
                    Directory.CreateDirectory(_options.MoveTo!);

                    // Never silently overwrite something already sitting there.
                    var target = destination;
                    for (var i = 1; File.Exists(target) && i < 1000; i++)
                        target = Path.Combine(_options.MoveTo!,
                            $"{Path.GetFileNameWithoutExtension(file)}_{i}{Path.GetExtension(file)}");

                    File.Move(file, target);
                    _logger.LogInformation("Watch: moved {File} to {Target}", file, target);
                    break;
            }
        }
        catch (Exception ex)
        {
            // The encode succeeded; failing to tidy up afterwards is not a reason to
            // report it as a failure, but it does need saying.
            _logger.LogError("Watch: encoded {File} but could not {Action} it — {Error}",
                file, _options.PostAction.ToString().ToLowerInvariant(), ex.Message);
        }
    }

    private void Remember(string file, bool succeeded, string? note = null)
    {
        long size = 0, ticks = 0;
        try
        {
            var info = new FileInfo(file);
            if (info.Exists) { size = info.Length; ticks = info.LastWriteTimeUtc.Ticks; }
        }
        catch { /* recorded with what we have */ }

        _handled[file] = new WatchedFile
        {
            Path = file,
            Size = size,
            ModifiedTicks = ticks,
            Succeeded = succeeded,
            HandledUtc = DateTime.UtcNow,
        };

        _pending.Remove(file);

        if (note is not null) _logger.LogDebug("Watch: skipping {File} ({Note})", file, note);
    }

    private Dictionary<string, WatchedFile> Load()
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        try
        {
            if (!File.Exists(_statePath)) return new Dictionary<string, WatchedFile>(comparer);

            var loaded = JsonSerializer.Deserialize<List<WatchedFile>>(File.ReadAllText(_statePath));
            return loaded is null
                ? new Dictionary<string, WatchedFile>(comparer)
                : loaded.GroupBy(f => f.Path, comparer)
                        .ToDictionary(g => g.Key, g => g.Last(), comparer);
        }
        catch (Exception ex)
        {
            // Losing the record means re-encoding, which is wasteful but not wrong.
            // Refusing to start because of it would be worse.
            _logger.LogWarning("Watch: could not read {Path} — starting with an empty record. {Error}",
                _statePath, ex.Message);
            return new Dictionary<string, WatchedFile>(comparer);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath,
                JsonSerializer.Serialize(_handled.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Watch: could not save {Path} — {Error}", _statePath, ex.Message);
        }
    }

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
