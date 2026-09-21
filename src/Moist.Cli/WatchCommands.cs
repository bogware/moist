using System.Text.Json;
using Moist.Services;

namespace Moist.Cli;

/// <summary>
/// <c>moist watch</c> — encode whatever lands in a folder.
///
/// Every option <c>moist encode</c> takes works here too, because each file is handed to
/// the same <see cref="EncodeRunner"/>. That is the point: a watch folder that encoded
/// differently from the command that set it up would be its own source of surprises.
/// </summary>
public static class WatchCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count == 0) throw new UsageException("watch requires a folder to watch");
        if (cl.Positional.Count > 1) throw new UsageException("watch takes one folder");

        var folder = Path.GetFullPath(cl.Positional[0]);
        if (!Directory.Exists(folder)) throw new UsageException($"folder not found: {folder}");

        var outputDirectory = cl.String("output-dir") ?? cl.String("output", "o");
        if (outputDirectory is null)
            throw new UsageException(
                "watch needs an output directory (--output-dir), and it must be outside the watched folder");

        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var postAction = (cl.String("on-success") ?? "keep").ToLowerInvariant() switch
        {
            "keep" => WatchPostAction.Keep,
            "move" => WatchPostAction.Move,
            "delete" => WatchPostAction.Delete,
            var other => throw new UsageException($"--on-success must be keep, move or delete (got '{other}')"),
        };

        var options = new WatchFolderOptions
        {
            Path = folder,
            OutputDirectory = outputDirectory,
            Recursive = cl.Flag("recursive", "r"),
            ScanInterval = TimeSpan.FromSeconds(cl.Int("interval") ?? 10),
            StableFor = TimeSpan.FromSeconds(cl.Int("stable-for") ?? 30),
            PostAction = postAction,
            MoveTo = cl.String("move-to") is { } move ? Path.GetFullPath(move) : null,
            IncludeExisting = !cl.Flag("new-only"),
            Extensions = cl.String("extensions") is { } list
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
                      .ToList()
                : new WatchFolderOptions { Path = folder }.Extensions,
        };

        using var services = Cli.CreateServices(cl);
        var quiet = cl.Flag("quiet", "q");
        var json = cl.Flag("json");
        var container = cl.String("container") ?? "mkv";

        WatchFolderService watcher;
        try
        {
            watcher = new WatchFolderService(options, services.LoggerFactory);
        }
        catch (ArgumentException ex)
        {
            // These are configuration mistakes with real consequences — an output folder
            // inside the watched one makes the watcher encode its own output forever.
            throw new UsageException(ex.Message);
        }

        Cli.WarnUnknown(cl);

        if (!quiet && !json)
        {
            Console.WriteLine($"Watching {folder}{(options.Recursive ? " (recursive)" : "")}");
            Console.WriteLine($"Output   {outputDirectory}");
            Console.WriteLine($"Settling {options.StableFor.TotalSeconds:N0}s, scanning every {options.ScanInterval.TotalSeconds:N0}s");
            if (postAction != WatchPostAction.Keep)
                Console.WriteLine($"On success: {postAction.ToString().ToLowerInvariant()}");
            Console.WriteLine("Press Ctrl+C to stop.\n");
        }

        var completed = 0;
        var failed = 0;

        try
        {
            await watcher.RunAsync(
                encode: async (file, token) =>
                {
                    // Preserve the source's subtree under the output dir. A recursive watch
                    // over per-title (or per-season) folders otherwise flattens everything
                    // to basename, so two 'episode.mkv' from different folders collide — and
                    // with --overwrite the second silently clobbers the first. For a
                    // non-recursive watch relativeDir is empty, so behaviour is unchanged.
                    var relativeDir = Path.GetDirectoryName(Path.GetRelativePath(folder, file)) ?? "";
                    var targetDir = Path.Combine(outputDirectory, relativeDir);
                    Directory.CreateDirectory(targetDir);
                    var output = Cli.ResolveOutput(
                        Path.Combine(targetDir, Path.GetFileNameWithoutExtension(file) + "." + container.TrimStart('.')),
                        overwrite: cl.Flag("overwrite", "y"));

                    if (!quiet && !json)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] encoding {Path.GetFileName(file)}");

                    var result = await EncodeRunner.RunOneAsync(cl, services, file, output, quiet || json, dryRun: false, token);
                    return new WatchResult(file, result.Output, result.Success, result.Error);
                },
                onFinished: result =>
                {
                    if (result.Success) completed++; else failed++;

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result));
                        Console.Out.Flush();
                    }
                    else if (!quiet)
                    {
                        Console.WriteLine(result.Success
                            ? $"[{DateTime.Now:HH:mm:ss}] done {Path.GetFileName(result.Output ?? result.Input)}"
                            : $"[{DateTime.Now:HH:mm:ss}] FAILED {Path.GetFileName(result.Input)} — {result.Error}");
                    }
                },
                ct);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C is how a watcher is meant to end, not a failure.
        }

        if (!quiet && !json)
            Console.WriteLine($"\nStopped. {completed} encoded, {failed} failed.");

        return failed > 0 ? Program.ExitFailed : Program.ExitOk;
    }
}

/// <summary>
/// <c>moist health</c> — find files in a library that are damaged or unplayable.
/// </summary>
public static class HealthCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count == 0) throw new UsageException("health requires a folder to scan");

        var root = Path.GetFullPath(cl.Positional[0]);
        if (!Directory.Exists(root)) throw new UsageException($"folder not found: {root}");

        using var services = Cli.CreateServices(cl);
        var json = cl.Flag("json");
        var quiet = cl.Flag("quiet", "q");
        var deep = cl.Flag("deep");

        var checker = new LibraryHealthChecker(services.MediaAnalyzer, services.Settings.FFmpegPath, services.LoggerFactory);

        var progress = quiet || json
            ? null
            : new Progress<(int Done, int Total, string File)>(p =>
            {
                if (p.Total == 0 || string.IsNullOrEmpty(p.File)) return;
                var name = Path.GetFileName(p.File);
                if (name.Length > 48) name = name[..45] + "…";
                Console.Error.Write($"\r[{p.Done + 1}/{p.Total}] {name,-50}");
            });

        if (!quiet && !json && deep)
            Console.Error.WriteLine("Deep scan: every file is decoded in full, which takes about as long as playing it.\n");

        var report = await checker.ScanAsync(
            root,
            recursive: !cl.Flag("no-recursive"),
            deep: deep,
            extensions: cl.String("extensions")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            progress: progress,
            ct: ct);

        if (progress is not null) Console.Error.Write("\r" + new string(' ', 62) + "\r");

        Cli.WarnUnknown(cl);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                scanned = report.FilesScanned,
                healthy = report.FilesHealthy,
                deep = report.DeepScan,
                elapsedSeconds = Math.Round(report.Elapsed.TotalSeconds, 1),
                issues = report.Issues.Select(i => new
                {
                    file = i.File,
                    kind = i.Kind.ToString(),
                    detail = i.Detail,
                    fault = i.IsFault,
                }),
            }, Cli.Json));
        }
        else
        {
            Report(report, deep);
        }

        // A non-zero exit lets this drive a cron job or a CI check.
        return report.Faults.Any() ? Program.ExitFailed : Program.ExitOk;
    }

    private static void Report(LibraryReport report, bool deep)
    {
        var faults = report.Faults.ToList();
        var notes = report.Issues.Where(i => !i.IsFault).ToList();

        if (faults.Count > 0)
        {
            Console.WriteLine($"Problems ({faults.Count}):\n");
            foreach (var group in faults.GroupBy(f => f.Kind))
            {
                Console.WriteLine($"  {Describe(group.Key)}");
                foreach (var issue in group)
                    Console.WriteLine($"    {issue.File}\n      {issue.Detail}");
                Console.WriteLine();
            }
        }

        if (notes.Count > 0)
        {
            Console.WriteLine($"Worth knowing ({notes.Count}):");
            foreach (var issue in notes)
                Console.WriteLine($"  {Path.GetFileName(issue.File)} — {issue.Detail}");
            Console.WriteLine();
        }

        // Counting issues here would overstate the damage: one broken file usually
        // trips several checks at once.
        var brokenFiles = faults.Select(f => f.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Console.WriteLine($"{report.FilesScanned} files scanned in {report.Elapsed.TotalSeconds:N1}s — " +
                          $"{report.FilesHealthy} healthy, {brokenFiles} with problems");

        if (!deep && faults.Count == 0 && report.FilesScanned > 0)
            Console.WriteLine(
                "\nThis read the container headers only, which catches interrupted writes but not " +
                "damage inside a file. Use --deep to decode everything and be sure.");
    }

    private static string Describe(LibraryIssueKind kind) => kind switch
    {
        LibraryIssueKind.Unreadable => "Cannot be read at all",
        LibraryIssueKind.NoVideoStream => "No video stream",
        LibraryIssueKind.ZeroDuration => "No duration — the write was probably interrupted",
        LibraryIssueKind.DecodeErrors => "Damaged — decoding produced errors",
        LibraryIssueKind.Truncated => "Truncated — fewer frames than the container claims",
        _ => kind.ToString(),
    };
}
