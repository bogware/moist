using System.Text.Json;
using Moist.Core.Jobs;
using Moist.Core.Encoders.Video;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Microsoft.Extensions.Logging;
using Moist.Services;

namespace Moist.Cli;

/// <summary>Shared setup: builds AppServices with CLI overrides applied.</summary>
public static class Cli
{
    public static AppServices CreateServices(CommandLine cl)
    {
        // Quiet by default: a CLI's stdout belongs to its output, not its logs.
        var level = cl.Flag("verbose", "v") ? LogLevel.Debug
                  : cl.Flag("quiet", "q") ? LogLevel.Error
                  : LogLevel.Warning;

        var services = new AppServices(level, logToStandardError: true);

        if (cl.String("ffmpeg") is { } ffmpeg) services.Settings.FFmpegPath = ffmpeg;
        if (cl.String("ffprobe") is { } ffprobe) services.Settings.FFprobePath = ffprobe;
        if (cl.String("temp-dir") is { } temp) services.Settings.TempDirectory = temp;

        var jobs = cl.Int("jobs", "j");
        if (jobs is not null)
        {
            if (jobs < 1) throw new UsageException("--jobs must be at least 1");
            services.JobQueue.MaxConcurrentJobs = jobs.Value;
        }

        return services;
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void WarnUnknown(CommandLine cl)
    {
        var unknown = cl.UnknownOptions().ToList();
        if (unknown.Count > 0)
            Console.Error.WriteLine($"warning: ignored unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }

    /// <summary>Auto-rename rather than clobbering, unless --overwrite was given.</summary>
    public static string ResolveOutput(string path, bool overwrite)
    {
        if (overwrite || !File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    public static IReadOnlyList<string> ExpandInputs(IEnumerable<string> patterns)
    {
        var results = new List<string>();
        foreach (var pattern in patterns)
        {
            if (File.Exists(pattern)) { results.Add(Path.GetFullPath(pattern)); continue; }

            // The shell may not have expanded the glob (cmd.exe never does).
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                var dir = Path.GetDirectoryName(pattern);
                dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
                var mask = Path.GetFileName(pattern);
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, mask).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        results.Add(Path.GetFullPath(f));
                }
                continue;
            }

            throw new UsageException($"input not found: {pattern}");
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public static class EncodeCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count == 0) throw new UsageException("encode requires an input file");
        if (cl.Positional.Count > 1)
            throw new UsageException($"encode takes one input (got {cl.Positional.Count}); use 'moist batch' for several");

        var input = Path.GetFullPath(cl.Positional[0]);
        if (!File.Exists(input)) throw new UsageException($"input not found: {input}");

        using var services = Cli.CreateServices(cl);
        var quiet = cl.Flag("quiet", "q");
        var json = cl.Flag("json");
        var dryRun = cl.Flag("dry-run");

        var output = cl.String("output", "o") is { } o
            ? Path.GetFullPath(o)
            : EncodeOptions.DefaultOutputPath(cl, services, input, null, "_encoded");

        output = Cli.ResolveOutput(output, cl.Flag("overwrite", "y"));

        var result = await EncodeRunner.RunOneAsync(cl, services, input, output, quiet || json, dryRun, ct);
        Cli.WarnUnknown(cl);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, Cli.Json));
        }
        else if (!result.Success)
        {
            Console.Error.WriteLine($"error: {result.Error ?? "encode failed"}");
        }
        else if (!dryRun && !quiet)
        {
            Console.WriteLine($"\nDone: {result.Output}  ({result.SizeBytes / 1024:N0} KB in {result.Elapsed})");
        }

        return result.Success ? Program.ExitOk : Program.ExitFailed;
    }
}

public static class BatchCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count == 0) throw new UsageException("batch requires at least one input");

        var inputs = Cli.ExpandInputs(cl.Positional);
        if (inputs.Count == 0) throw new UsageException("no input files matched");

        using var services = Cli.CreateServices(cl);
        var quiet = cl.Flag("quiet", "q");
        var json = cl.Flag("json");
        var dryRun = cl.Flag("dry-run");
        var keepGoing = cl.Flag("keep-going");
        var outputDir = cl.String("output-dir") is { } d ? Path.GetFullPath(d) : null;
        var suffix = cl.String("suffix") ?? "_encoded";

        if (outputDir is not null) Directory.CreateDirectory(outputDir);

        if (!quiet && !json)
            Console.WriteLine($"Encoding {inputs.Count} file(s)\n");

        var results = new List<EncodeResult>();
        var index = 0;

        foreach (var input in inputs)
        {
            index++;
            if (ct.IsCancellationRequested) break;

            var output = Cli.ResolveOutput(
                EncodeOptions.DefaultOutputPath(cl, services, input, outputDir, suffix),
                cl.Flag("overwrite", "y"));

            if (!quiet && !json)
                Console.WriteLine($"[{index}/{inputs.Count}] {Path.GetFileName(input)}");

            var r = await EncodeRunner.RunOneAsync(cl, services, input, output, quiet || json, dryRun, ct);
            results.Add(r);

            if (!r.Success && !keepGoing)
            {
                if (!json) Console.Error.WriteLine("Stopping (pass --keep-going to continue after failures).");
                break;
            }
        }

        Cli.WarnUnknown(cl);

        var ok = results.Count > 0 && results.All(r => r.Success);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                total = inputs.Count,
                attempted = results.Count,
                succeeded = results.Count(r => r.Success),
                failed = results.Count(r => !r.Success),
                results,
            }, Cli.Json));
        }
        else if (!dryRun)
        {
            var good = results.Count(r => r.Success);
            Console.WriteLine($"\n{good}/{results.Count} succeeded");
            foreach (var f in results.Where(r => !r.Success))
                Console.Error.WriteLine($"  failed: {Path.GetFileName(f.Input)} — {f.Error}");
        }

        return ok ? Program.ExitOk : Program.ExitFailed;
    }
}

public sealed class EncodeResult
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long SizeBytes { get; init; }
    public string Elapsed { get; init; } = "0s";
    public List<string> Commands { get; init; } = [];
}

public static class EncodeRunner
{
    public static async Task<EncodeResult> RunOneAsync(
        CommandLine cl, AppServices services, string input, string output, bool quiet, bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;

        MediaFileInfo? info = null;
        try
        {
            info = await services.MediaAnalyzer.AnalyzeAsync(input, ct);
        }
        catch (Exception ex)
        {
            return new EncodeResult
            {
                Input = input, Output = output, Success = false,
                Error = $"could not analyze input ({ex.Message}). Is FFprobe installed? Try 'moist tools'.",
            };
        }

        var (filterScript, filterEngineId) = EncodeOptions.BuildFilters(cl, services, input);
        var request = EncodeOptions.Build(
            cl, services, input, output, filterScript, filterEngineId,
            sourceAudioTrackCount: info.AudioStreams.Count);

        if (EncodeJobBuilder.Validate(request) is { } invalid)
            return new EncodeResult { Input = input, Output = output, Success = false, Error = invalid };

        // A GPU encoder that isn't present fails deep inside FFmpeg with an unhelpful
        // message. Probe first and say something useful instead.
        if (!request.AudioOnly && HardwareCapabilities.Find(request.VideoEncoderId) is not null)
        {
            await services.DetectHardwareAsync(ct: ct);
            if (!services.Hardware.IsUsable(request.VideoEncoderId))
            {
                var status = services.Hardware.Statuses.FirstOrDefault(s => s.Encoder.Id == request.VideoEncoderId);
                var why = status?.Reason ?? "not available on this machine";
                return new EncodeResult
                {
                    Input = input,
                    Output = output,
                    Success = false,
                    Error = $"{request.VideoEncoderId} is unavailable: {why}. Run 'moist list hardware' to see what works here.",
                };
            }
        }

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        if (dryRun)
        {
            Console.WriteLine($"  input   : {input}");
            Console.WriteLine($"  output  : {output}");
            Console.WriteLine($"  video   : {request.VideoEncoderId} {request.Mode} q={request.Quality} bitrate={request.Bitrate}k preset={request.Preset}");
            Console.WriteLine($"  audio   : {request.AudioEncoderId} {request.AudioBitrateMode} {request.AudioBitrate}k");
            Console.WriteLine($"  container: {request.Container}{(request.AudioOnly ? " (audio only)" : "")}");
            if (filterScript is not null) Console.WriteLine($"  filters : {filterScript}");
            Console.WriteLine($"  jobs    : {string.Join(" -> ", jobs.Select(j => j.JobType))}");
            Console.WriteLine();
            return new EncodeResult { Input = input, Output = output, Success = true, Elapsed = "0s" };
        }

        foreach (var job in jobs) services.JobQueue.Enqueue(job);

        var runner = new QueueRunner(services, showProgress: !quiet && !cl.Flag("no-progress"), quiet);
        var ok = await runner.RunAsync(jobs.Select(j => j.Id).ToList(), ct);

        var failed = jobs.FirstOrDefault(j => j.Status == JobStatus.Failed);
        var elapsed = DateTime.UtcNow - started;

        return new EncodeResult
        {
            Input = input,
            Output = output,
            Success = ok && File.Exists(output),
            Error = ok ? (File.Exists(output) ? null : "output file was not produced") : Summarize(failed),
            SizeBytes = File.Exists(output) ? new FileInfo(output).Length : 0,
            Elapsed = QueueRunner.Format(elapsed),
            Commands = jobs.Select(j => j.OutputLog.FirstOrDefault(l => l.StartsWith('[')) ?? "").Where(s => s.Length > 0).ToList(),
        };
    }

    private static string Summarize(Job? failed)
    {
        if (failed?.ErrorMessage is null) return "encode failed";
        var lines = failed.ErrorMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                       .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? "encode failed" : string.Join(" | ", lines.TakeLast(3));
    }
}

public static class ProbeCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count == 0) throw new UsageException("probe requires an input file");
        var input = Path.GetFullPath(cl.Positional[0]);
        if (!File.Exists(input)) throw new UsageException($"input not found: {input}");

        using var services = Cli.CreateServices(cl);
        MediaFileInfo info;
        try
        {
            info = await services.MediaAnalyzer.AnalyzeAsync(input, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Program.ExitMissingTool;
        }

        if (cl.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(info, Cli.Json));
            return Program.ExitOk;
        }

        Console.WriteLine($"File     : {input}");
        Console.WriteLine($"Duration : {info.Duration:hh\\:mm\\:ss\\.ff}");

        if (info.Video is { } v)
        {
            Console.WriteLine($"Video    : {v.Codec} {v.Width}x{v.Height} @ {v.Framerate:F3} fps" +
                              (v.TotalFrames > 0 ? $" ({v.TotalFrames:N0} frames)" : ""));
        }
        else Console.WriteLine("Video    : none");

        if (info.AudioStreams.Count == 0) Console.WriteLine("Audio    : none");
        for (var i = 0; i < info.AudioStreams.Count; i++)
        {
            var a = info.AudioStreams[i];
            Console.WriteLine($"Audio #{i}  : {a.Codec} {a.Channels}ch" +
                              (a.Language is not null ? $" [{a.Language}]" : "") +
                              (a.Title is not null ? $" — {a.Title}" : ""));
        }

        for (var i = 0; i < info.SubtitleStreams.Count; i++)
        {
            var sub = info.SubtitleStreams[i];
            Console.WriteLine($"Sub #{i}    : {sub.Codec}" +
                              (sub.Language is not null ? $" [{sub.Language}]" : "") +
                              (sub.IsForced ? " (forced)" : "") +
                              (sub.IsDefault ? " (default)" : ""));
        }

        return Program.ExitOk;
    }
}

public static class ListCommand
{
    public static int Run(CommandLine cl)
    {
        var what = (cl.Positional.FirstOrDefault() ?? "encoders").ToLowerInvariant();
        using var services = Cli.CreateServices(cl);
        var json = cl.Flag("json");

        switch (what)
        {
            case "hardware":
            case "hw":
            {
                var statuses = services.DetectHardwareAsync().GetAwaiter().GetResult();

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(statuses.Select(s => new
                    {
                        id = s.Encoder.Id,
                        name = s.Encoder.DisplayName,
                        vendor = s.Encoder.Vendor.ToString(),
                        codec = s.Encoder.Codec,
                        compiledIn = s.CompiledIn,
                        usable = s.Usable,
                        reason = s.Reason,
                    }), Cli.Json));
                    return Program.ExitOk;
                }

                foreach (var s in statuses.OrderBy(s => s.Encoder.Vendor).ThenBy(s => s.Encoder.Id))
                {
                    var mark = s.Usable ? "ok " : "-- ";
                    Console.WriteLine($"  {mark} {s.Encoder.Id,-18} {s.Encoder.DisplayName}");
                    if (!s.Usable && s.Reason is not null)
                        Console.WriteLine($"       {s.Reason}");
                }

                var count = statuses.Count(s => s.Usable);
                Console.WriteLine($"\n{count} hardware encoder(s) usable on this machine.");
                return Program.ExitOk;
            }

            case "encoders":
            case "video-encoders":
            case "audio-encoders":
            {
                // Only advertise hardware encoders that were proven to work.
                services.DetectHardwareAsync().GetAwaiter().GetResult();
                var video = services.GetUsableVideoEncoders().OrderBy(e => e.Id).ToList();
                var audio = services.Encoders.GetAudioEncoders().OrderBy(e => e.Id).ToList();

                if (json)
                {
                    object payload = what switch
                    {
                        "video-encoders" => new { video = video.Select(Describe) },
                        "audio-encoders" => new { audio = audio.Select(Describe) },
                        _ => new { video = video.Select(Describe), audio = audio.Select(Describe) },
                    };
                    Console.WriteLine(JsonSerializer.Serialize(payload, Cli.Json));
                    return Program.ExitOk;
                }

                if (what != "audio-encoders")
                {
                    Console.WriteLine("VIDEO");
                    foreach (var e in video) Console.WriteLine($"  {e.Id,-22} {e.Name}");
                }
                if (what != "video-encoders")
                {
                    if (what != "audio-encoders") Console.WriteLine();
                    Console.WriteLine("AUDIO");
                    foreach (var e in audio) Console.WriteLine($"  {e.Id,-22} {e.Name}");
                }
                return Program.ExitOk;
            }

            case "containers":
            {
                var rows = Enum.GetValues<ContainerFormat>().Select(c => new
                {
                    container = c.ToString(),
                    extension = c.GetExtension(),
                    video = Enum.GetValues<ContainerFormat>().Length > 0
                        ? new[] { "h264", "hevc", "vp9", "av1", "ffv1" }.Where(v => ContainerCompatibility.SupportsVideo(c, v)).ToArray()
                        : [],
                    audio = new[] { "aac", "opus", "mp3", "flac", "ac3", "eac3", "vorbis" }
                        .Where(a => ContainerCompatibility.SupportsAudio(c, a)).ToArray(),
                }).ToList();

                if (json) { Console.WriteLine(JsonSerializer.Serialize(rows, Cli.Json)); return Program.ExitOk; }

                foreach (var r in rows)
                {
                    Console.WriteLine($"{r.container,-6} {r.extension}");
                    Console.WriteLine($"       video: {string.Join(", ", r.video)}");
                    Console.WriteLine($"       audio: {string.Join(", ", r.audio)}");
                }
                return Program.ExitOk;
            }

            case "profiles":
            {
                var profiles = services.ProfileManager.GetAllProfiles().OrderBy(p => p.Name).ToList();
                if (json) { Console.WriteLine(JsonSerializer.Serialize(profiles, Cli.Json)); return Program.ExitOk; }

                foreach (var p in profiles)
                {
                    Console.WriteLine($"  {p.Name}");
                    Console.WriteLine($"      {p.VideoEncoderId} {p.VideoMode} / {p.AudioEncoderId} {p.AudioBitrate}k / {p.Container}");
                    if (!string.IsNullOrWhiteSpace(p.Description)) Console.WriteLine($"      {p.Description}");
                }
                return Program.ExitOk;
            }

            case "filters":
            {
                var engine = services.Filters.Engines.FirstOrDefault(e => e.Id == "ffmpeg");
                if (engine is null) { Console.Error.WriteLine("error: FFmpeg filter engine not registered"); return Program.ExitFailed; }

                var filters = engine.GetAvailableFilters().OrderBy(f => f.Id).ToList();
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(filters.Select(f => new
                    {
                        f.Id, f.Name, category = f.Category.ToString(), f.Description,
                        parameters = f.Parameters.Select(p => new { key = p.Key, p.Value.Name, type = p.Value.Type.ToString(), p.Value.DefaultValue }),
                    }), Cli.Json));
                    return Program.ExitOk;
                }

                foreach (var f in filters)
                {
                    Console.WriteLine($"  {f.Id,-14} {f.Name}");
                    if (f.Parameters.Count > 0)
                        Console.WriteLine($"      params: {string.Join(", ", f.Parameters.Select(p => $"{p.Key}={p.Value.DefaultValue}"))}");
                }
                return Program.ExitOk;
            }

            default:
                throw new UsageException(
                    $"don't know how to list '{what}'. Try encoders, hardware, containers, profiles or filters.");
        }

        static object Describe(Moist.Core.Encoders.EncoderInfo e) => new { e.Id, e.Name, e.Description };
    }
}

public static class ToolsCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        using var services = Cli.CreateServices(cl);
        var statuses = await services.DiscoverToolsAsync(ct);

        if (cl.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(statuses.Select(s => new
            {
                id = s.Tool.Id,
                name = s.Tool.Name,
                required = s.Tool.IsRequired,
                available = s.IsAvailable,
                path = s.DetectedPath,
                version = s.Version,
            }), Cli.Json));
        }
        else
        {
            foreach (var s in statuses)
            {
                var mark = s.IsAvailable ? "ok " : (s.Tool.IsRequired ? "MISSING" : "--");
                Console.WriteLine($"  {mark,-8} {s.Tool.Name,-22} {s.DetectedPath ?? "(not found)"}");
                if (s.Version is not null) Console.WriteLine($"           {s.Version.Trim()}");
            }
        }

        var missingRequired = statuses.Where(s => s.Tool.IsRequired && !s.IsAvailable).ToList();
        if (missingRequired.Count > 0)
        {
            Console.Error.WriteLine($"\nMissing required tool(s): {string.Join(", ", missingRequired.Select(s => s.Tool.Name))}");
            return Program.ExitMissingTool;
        }

        return Program.ExitOk;
    }
}
