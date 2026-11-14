using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moist.Core.Jobs;
using Moist.Services;

namespace Moist.Cli;

/// <summary>
/// <c>moist serve</c> — a small HTTP API over the same queue the GUI and CLI use.
///
/// Built on <see cref="HttpListener"/> rather than ASP.NET Core deliberately: it is in the
/// base library, so the self-contained binary stays one file and gains no dependencies for
/// a handful of endpoints.
///
/// Note what this API can do. An encode names its own input and output paths and can carry
/// custom encoder arguments, so anyone who can reach it can read and write files as
/// whoever runs the server. It is therefore authenticated by default and bound to
/// loopback by default, and refuses to do neither.
/// </summary>
public static class ServeCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        var host = cl.String("host") ?? "127.0.0.1";
        var port = cl.Int("port") ?? 7788;
        // MOIST_TOKEN is read here rather than passed on the command line by wrappers (the Docker
        // entrypoint used to), because argv is visible to every user on the host via ps / /proc.
        var token = cl.String("token")
                    ?? (Environment.GetEnvironmentVariable("MOIST_TOKEN") is { Length: > 0 } envToken ? envToken : null);
        var open = cl.Flag("no-auth");
        var roots = cl.Many("root").Select(r => Path.GetFullPath(r)).ToList();
        foreach (var r in roots)
            if (!Directory.Exists(r))
                throw new UsageException($"--root {r}: no such directory");

        if (open && token is not null)
            throw new UsageException("--token and --no-auth contradict each other");

        // IPAddress.IsLoopback covers all of 127.0.0.0/8 and ::1, so 127.0.0.2 counts as
        // loopback too — the old exact-string test misclassified it as public.
        var loopback = IPAddress.TryParse(host, out var ip) ? IPAddress.IsLoopback(ip)
                       : host is "localhost";

        if (open && !loopback)
            throw new UsageException(
                $"--no-auth is only allowed on loopback. Serving {host} unauthenticated would let " +
                "anyone who can reach this machine read and write files as the user running it.");

        // An encode names its own input and output paths. On loopback that is the local user's own
        // reach; on a network it is a stranger's, so the folders they may touch must be named.
        if (!loopback && roots.Count == 0)
            throw new UsageException(
                $"serving {host} needs at least one --root <dir>: the folders remote encodes may read and write. " +
                "Without it any token holder could read or overwrite any file this user can.");

        if (!open && token is null)
        {
            // Generating one is friendlier than refusing, and still leaves the operator
            // with something they must copy — unlike a default that everyone shares.
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            Console.WriteLine($"Generated access token: {token}");
            Console.WriteLine("Pass it as 'Authorization: Bearer <token>'.\n");
        }

        using var services = Cli.CreateServices(cl);

        // `moist serve` is long-lived: without a cap, JobQueue would retain every job it ever ran —
        // and each job's rolling output-log buffer — for the life of the daemon. Bound it so memory
        // stays flat over days of encodes. (Per-job progress entries are evicted separately in
        // MoistHttpApi.TrackProgress when a job settles.)
        services.JobQueue.MaxCompletedJobsRetained = 200;

        // When bound to loopback, only these Host authorities are ours; anything else is a
        // page that resolved another hostname to a loopback address (DNS rebinding).
        var loopbackHosts = loopback
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
              {
                  $"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}", $"{host}:{port}",
              }
            : null;

        var api = new MoistHttpApi(services, token, loopbackHosts, new ServePolicy(roots));

        using var listener = new HttpListener();
        // Reserve the wildcard only for an explicit all-interfaces request; a specific host —
        // loopback or a single NIC — binds only that address rather than silently listening on
        // every interface, which is what `+` did for any non-loopback host.
        var bindHost = host is "0.0.0.0" or "*" or "+" or "::" ? "+" : host;
        listener.Prefixes.Add($"http://{bindHost}:{port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Binding anything but loopback needs a URL reservation on Windows, which is
            // the usual cause and is not obvious from the raw error.
            throw new UsageException(
                $"could not listen on {host}:{port} — {ex.Message}." +
                (loopback ? "" : $"\nOn Windows, binding a non-loopback address needs: " +
                                 $"netsh http add urlacl url=http://{bindHost}:{port}/ user=\"%USERNAME%\""));
        }

        Cli.WarnUnknown(cl);

        Console.WriteLine($"moist serving on http://{host}:{port}/");
        Console.WriteLine(open ? "Authentication disabled (loopback only)." : "Authentication required.");
        Console.WriteLine(roots.Count > 0
            ? $"Requests confined to: {string.Join(", ", roots)}"
            : "Requests are not confined to a folder (loopback only). Use --root to restrict them.");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        // Stopping the listener is what unblocks GetContextAsync.
        using var registration = ct.Register(() => { try { listener.Stop(); } catch { } });

        var inFlight = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            inFlight.Add(api.HandleAsync(context, ct));
            inFlight.RemoveAll(t => t.IsCompleted);
        }

        try { await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch { /* shutting down anyway */ }

        services.JobQueue.Stop();
        Console.WriteLine("\nStopped.");
        return Program.ExitOk;
    }
}

/// <summary>Routes and serves the API.</summary>
/// <param name="loopbackHosts">
/// When the listener is bound to loopback, the set of acceptable <c>Host</c> authorities
/// (e.g. <c>127.0.0.1:7788</c>). A request whose Host is not one of them is a DNS-rebinding
/// attempt and is refused. <c>null</c> disables the check (a deliberately exposed bind).
/// </param>
public sealed class MoistHttpApi(AppServices services, string? token, IReadOnlyCollection<string>? loopbackHosts = null,
                                 ServePolicy? policy = null)
{
    /// <summary>What remote callers may ask for; unrestricted paths unless roots were given.</summary>
    private readonly ServePolicy _policy = policy ?? new ServePolicy(null);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private Task? _queueRun;
    private readonly object _queueLock = new();

    /// <summary>
    /// Latest progress per job. A Job carries no progress of its own — the queue reports
    /// it as an event — so the API has to keep it to answer a poll.
    /// </summary>
    private readonly ConcurrentDictionary<string, double> _progress = new();

    private bool _subscribed;

    private void TrackProgress()
    {
        lock (_queueLock)
        {
            if (_subscribed) return;
            services.JobQueue.JobProgressUpdated += (_, e) => _progress[e.Job.Id] = e.Progress.Percentage;
            // Evict a job's progress once it settles, so a long-lived daemon doesn't retain an
            // entry per job forever. Describe() reports 100 for a Completed job unconditionally,
            // so this only changes the last-seen percent of a Failed/Cancelled job (then 0) —
            // cosmetic.
            services.JobQueue.JobStatusChanged += (_, job) =>
            {
                if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                    _progress.TryRemove(job.Id, out double _);
            };
            _subscribed = true;
        }
    }

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";

        try
        {
            // DNS-rebinding guard (loopback binds only): a request whose Host authority is not
            // one of ours came from a page that resolved some other hostname to a loopback
            // address, hoping to borrow our trusted-local-caller status. Refuse it.
            if (loopbackHosts is not null && !loopbackHosts.Contains(request.UserHostName ?? ""))
            {
                await WriteAsync(context, 421, new { error = "misdirected request" });
                return;
            }

            if (!Authorized(request))
            {
                await WriteAsync(context, 401, new { error = "unauthorized" });
                return;
            }

            var handled = (request.HttpMethod, path) switch
            {
                ("GET", "" or "/api" or "/api/health") => await HealthAsync(context),
                ("GET", "/api/tools") => await ToolsAsync(context, ct),
                ("GET", "/api/probe") => await ProbeAsync(context, ct),
                ("GET", "/api/queue") => await QueueAsync(context),
                ("POST", "/api/encode") => await EncodeAsync(context, ct),
                ("POST", "/api/queue/start") => await StartAsync(context),
                ("POST", "/api/queue/stop") => await StopAsync(context),
                _ => await NotFoundAsync(context, path),
            };

            _ = handled;
        }
        catch (Exception ex)
        {
            // Never return a stack trace: it names paths and internals to whoever asked.
            services.LoggerFactory?.CreateLogger("api").LogError(ex, "Request {Path} failed", path);
            try { await WriteAsync(context, 500, new { error = ex.Message }); } catch { }
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        if (token is null) return true;

        // Header only: a token in the query string leaks into proxy logs, browser history
        // and Referer headers — the same credential that gates the filesystem trust boundary.
        var header = request.Headers["Authorization"];
        var presented = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? header["Bearer ".Length..].Trim()
            : null;

        if (presented is null) return false;

        // Fixed-time so the comparison can't be used to recover the token a byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
    }

    private async Task<bool> HealthAsync(HttpListenerContext context)
    {
        await WriteAsync(context, 200, new
        {
            status = "ok",
            version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            queue = new
            {
                running = services.JobQueue.IsRunning,
                jobs = services.JobQueue.Jobs.Count,
            },
        });
        return true;
    }

    private async Task<bool> ToolsAsync(HttpListenerContext context, CancellationToken ct)
    {
        var tools = await services.DiscoverToolsAsync(ct);
        await WriteAsync(context, 200, tools.Select(t => new
        {
            id = t.Tool.Id,
            name = t.Tool.Name,
            found = t.IsAvailable,
            path = t.DetectedPath,
            version = t.Version,
            required = t.Tool.IsRequired,
        }));
        return true;
    }

    private async Task<bool> ProbeAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.QueryString["path"];
        if (string.IsNullOrWhiteSpace(path))
        {
            await WriteAsync(context, 400, new { error = "path is required" });
            return true;
        }

        if (_policy.Resolve(path, "path", out var refused) is not { } probePath)
        {
            await WriteAsync(context, 403, new { error = refused });
            return true;
        }

        if (!File.Exists(probePath))
        {
            await WriteAsync(context, 404, new { error = $"not found: {probePath}" });
            return true;
        }

        var info = await services.MediaAnalyzer.AnalyzeAsync(probePath, ct);
        await WriteAsync(context, 200, new
        {
            file = info.FilePath,
            durationSeconds = Math.Round(info.Duration.TotalSeconds, 3),
            sizeBytes = info.FileSizeBytes,
            video = info.Video is null ? null : new
            {
                codec = info.Video.Codec,
                width = info.Video.Width,
                height = info.Video.Height,
                frameRate = info.Video.Framerate,
                frames = info.Video.TotalFrames,
                bitDepth = info.Video.BitDepth,
                pixelFormat = info.Video.PixelFormat,
            },
            audio = info.AudioStreams.Select(a => new { codec = a.Codec, channels = a.Channels, language = a.Language }),
            subtitles = info.SubtitleStreams.Select(s => new { codec = s.Codec, language = s.Language }),
        });
        return true;
    }

    private async Task<bool> QueueAsync(HttpListenerContext context)
    {
        await WriteAsync(context, 200, new
        {
            running = services.JobQueue.IsRunning,
            jobs = services.JobQueue.Jobs.Values.Select(Describe),
        });
        return true;
    }

    private object Describe(Job job) => new
    {
        id = job.Id,
        name = job.Name,
        type = job.JobType,
        status = job.Status.ToString(),
        // Short jobs can finish without ever reporting progress — a mux of two temp files
        // takes a fraction of a second — and a finished job reading 0% looks stuck.
        percent = job.Status == JobStatus.Completed
            ? 100
            : Math.Round(_progress.GetValueOrDefault(job.Id), 1),
        input = job.InputFile,
        output = job.OutputFile,
        error = job.ErrorMessage,
    };

    private const long MaxBodyBytes = 1 << 20; // 1 MiB; encode requests are tiny JSON.

    /// <summary>
    /// Queue an encode. The body names the input and output, plus any option
    /// <c>moist encode</c> accepts:
    ///
    /// <code>{"input": "/media/in.mkv", "output": "/media/out.mkv",
    ///        "options": {"crf": 22, "preset": "slow", "chunked": true}}</code>
    ///
    /// Those options are turned back into a command line and parsed by the same code the
    /// CLI uses, so there is one definition of what an option means rather than two that
    /// drift.
    ///
    /// Clients must send <c>Content-Type: application/json</c>: requiring a non-simple content
    /// type forces a CORS preflight, so a cross-site page cannot reach this file-touching
    /// endpoint with a "simple" request the same-origin policy would otherwise let through.
    /// </summary>
    private async Task<bool> EncodeAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!IsJsonContentType(context.Request.ContentType))
        {
            await WriteAsync(context, 415, new { error = "Content-Type must be application/json" });
            return true;
        }

        if (context.Request.ContentLength64 > MaxBodyBytes)
        {
            await WriteAsync(context, 413, new { error = $"request body too large (max {MaxBodyBytes} bytes)" });
            return true;
        }

        // Bounded read: abort past the cap rather than buffering an arbitrarily large (or
        // chunked, unknown-length) body into a managed string and OOM-crashing the daemon.
        string body;
        using (var ms = new MemoryStream())
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.InputStream.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > MaxBodyBytes)
                {
                    await WriteAsync(context, 413, new { error = $"request body too large (max {MaxBodyBytes} bytes)" });
                    return true;
                }
                ms.Write(buffer, 0, read);
            }
            body = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        EncodeApiRequest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EncodeApiRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            await WriteAsync(context, 400, new { error = $"invalid JSON: {ex.Message}" });
            return true;
        }

        if (payload?.Input is null)
        {
            await WriteAsync(context, 400, new { error = "input is required" });
            return true;
        }

        if (ServePolicy.CheckOptions(payload.Options) is { } optionError)
        {
            await WriteAsync(context, 400, new { error = optionError });
            return true;
        }

        // Everything about the options is settled before the filesystem is touched: a request that
        // will be refused learns nothing about which paths exist.
        CommandLine cl;
        try
        {
            cl = CommandLine.Parse(ToArguments(payload.Options));
        }
        catch (UsageException ex)
        {
            await WriteAsync(context, 400, new { error = ex.Message });
            return true;
        }

        // Belt and braces: check what the parser actually produced, not just the JSON keys.
        if (ServePolicy.CheckParsed(cl.OptionNames, cl.Positional) is { } parsedError)
        {
            await WriteAsync(context, 400, new { error = parsedError });
            return true;
        }

        if (_policy.Resolve(payload.Input, "input", out var inputError) is not { } input)
        {
            await WriteAsync(context, 403, new { error = inputError });
            return true;
        }
        if (!File.Exists(input))
        {
            await WriteAsync(context, 404, new { error = $"input not found: {input}" });
            return true;
        }

        var output = payload.Output is { } requested
            ? requested
            : EncodeOptions.DefaultOutputPath(cl, services, input, null, "_encoded");
        if (_policy.Resolve(output, "output", out var outputError) is not { } confinedOutput)
        {
            await WriteAsync(context, 403, new { error = outputError });
            return true;
        }
        output = confinedOutput;

        // An external subtitle file is read into the encode, so it is a path like any other.
        if (cl.String("subtitle") is { } subtitle)
        {
            if (_policy.Resolve(subtitle, "subtitle", out var subtitleError) is null)
            {
                await WriteAsync(context, 403, new { error = subtitleError });
                return true;
            }
        }

        try
        {
            var info = await services.MediaAnalyzer.AnalyzeAsync(input, ct);
            var (filterScript, filterEngineId) = EncodeOptions.BuildFilters(cl, services, input);
            var request = EncodeOptions.Build(cl, services, input, output, filterScript, filterEngineId,
                sourceAudioTrackCount: info.AudioStreams.Count);

            if (EncodeJobBuilder.Validate(request, info) is { } invalid)
            {
                await WriteAsync(context, 400, new { error = invalid });
                return true;
            }

            var plan = EncodeJobBuilder.Build(request, info, ffprobePath: services.Settings.FFprobePath);
            var jobs = plan.AllJobs.ToList();
            foreach (var job in jobs) services.JobQueue.Enqueue(job);

            services.QueueStore.Record(request, plan.Mux?.Id ?? jobs[^1].Id);

            await WriteAsync(context, 202, new
            {
                queued = jobs.Count,
                output,
                jobs = jobs.Select(Describe),
                warnings = EncodeJobBuilder.Warnings(request, info, chunked: plan.IsChunked),
            });
        }
        catch (UsageException ex)
        {
            await WriteAsync(context, 400, new { error = ex.Message });
        }

        return true;
    }

    /// <summary>
    /// Turn {"crf": 22, "chunked": true} into ["--crf", "22", "--chunked"], so the API and
    /// the CLI share one option parser.
    /// </summary>
    private static IEnumerable<string> ToArguments(Dictionary<string, JsonElement>? options)
    {
        if (options is null) yield break;

        foreach (var (key, value) in options)
        {
            var name = "--" + key.TrimStart('-');

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    yield return name;
                    break;

                case JsonValueKind.False:
                case JsonValueKind.Null:
                    break;

                // A value always travels in the same token as its name (--name=value). Sent as a
                // separate token, a value after a no-value flag was parsed as an option of its own:
                // {"overwrite": "--video-args=…"} slipped a forbidden option past the allow-list.
                case JsonValueKind.Array:
                    // Repeatable options such as --audio-track.
                    foreach (var item in value.EnumerateArray())
                        yield return $"{name}={item}";
                    break;

                default:
                    yield return $"{name}={value}";
                    break;
            }
        }
    }

    private async Task<bool> StartAsync(HttpListenerContext context)
    {
        lock (_queueLock)
        {
            // StartAsync runs until the queue is stopped; starting a second one would
            // process every job twice.
            if (_queueRun is null || _queueRun.IsCompleted)
                _queueRun = services.JobQueue.StartAsync();
        }

        TrackProgress();

        await WriteAsync(context, 200, new { running = true, jobs = services.JobQueue.Jobs.Count });
        return true;
    }

    private async Task<bool> StopAsync(HttpListenerContext context)
    {
        services.JobQueue.Stop();
        await WriteAsync(context, 200, new { running = false });
        return true;
    }

    private static async Task<bool> NotFoundAsync(HttpListenerContext context, string path)
    {
        await WriteAsync(context, 404, new
        {
            error = $"no route for {context.Request.HttpMethod} {path}",
            routes = new[]
            {
                "GET  /api/health",
                "GET  /api/tools",
                "GET  /api/probe?path=<file>",
                "GET  /api/queue",
                "POST /api/encode",
                "POST /api/queue/start",
                "POST /api/queue/stop",
            },
        });
        return true;
    }

    /// <summary>True when the Content-Type's media type is exactly <c>application/json</c>,
    /// ignoring any <c>; charset=…</c> parameter.</summary>
    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        var semicolon = contentType.IndexOf(';');
        var media = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, object body)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = payload.Length;

        await context.Response.OutputStream.WriteAsync(payload);
        context.Response.Close();
    }
}

public sealed class EncodeApiRequest
{
    public string? Input { get; set; }
    public string? Output { get; set; }
    public Dictionary<string, JsonElement>? Options { get; set; }
}
