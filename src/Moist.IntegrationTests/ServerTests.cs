using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Moist.Cli;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// The HTTP API.
///
/// It runs against a real <see cref="HttpListener"/> rather than a mocked one, because
/// most of what could go wrong here — authorization, status codes, how a JSON body turns
/// into encode options — only exists at that boundary.
/// </summary>
[Collection("media")]
public sealed class ServerTests(MediaFixture media) : PipelineTestBase(media), IDisposable
{
    private const string Token = "test-token-value";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
    }

    /// <summary>Start the API on a free port and return a client pointed at it.</summary>
    private (HttpClient Client, AppServices Services) Start(string? token = Token)
    {
        var services = CreateServices();
        _disposables.Add(services);

        var api = new MoistHttpApi(services, token);

        // Ports are picked by trying: asking the OS for a free one and then binding a
        // second time races anything else on the machine doing the same.
        for (var port = 17800; port < 17900; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try { listener.Start(); }
            catch (HttpListenerException) { continue; }

            _disposables.Add(new Stopper(listener));

            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch { return; }

                    _ = api.HandleAsync(context, CancellationToken.None);
                }
            });

            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            if (token is not null)
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            _disposables.Add(client);
            return (client, services);
        }

        throw new InvalidOperationException("no free port in 17800-17899 to run the test server on");
    }

    private sealed class Stopper(HttpListener listener) : IDisposable
    {
        public void Dispose() { try { listener.Stop(); listener.Close(); } catch { } }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    // ---------------- authorization ----------------

    [Fact]
    public async Task WithoutATokenNothingIsServed()
    {
        // The API can read and write files anywhere the server process can, so an
        // unauthenticated request reaching anything at all would be the whole problem.
        var (client, _) = Start();
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWrongTokenIsRejected()
    {
        var (client, _) = Start();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task ATokenOfTheWrongLengthIsRejectedRatherThanCrashing()
    {
        // Fixed-time comparison throws on unequal lengths if it isn't guarded.
        var (client, _) = Start();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "x");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task TheTokenIsRejectedInTheQueryString()
    {
        // A token in the URL leaks into server logs, proxy logs and browser history, so the
        // query-string fallback was removed — the credential must travel in the Authorization
        // header only. A ?token= request is unauthenticated and must be refused.
        var (client, _) = Start();
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.GetAsync($"/api/health?token={Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithAuthenticationOffEverythingIsServed()
    {
        var (client, _) = Start(token: null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    // ---------------- routes ----------------

    [Fact]
    public async Task HealthReportsTheVersion()
    {
        var (client, _) = Start();

        var body = await BodyAsync(await client.GetAsync("/api/health"));

        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task AnUnknownRouteSaysWhatTheKnownOnesAre()
    {
        var (client, _) = Start();

        var response = await client.GetAsync("/api/nonsense");
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEmpty(body.GetProperty("routes").EnumerateArray());
    }

    [FfmpegFact]
    public async Task ProbeReportsTheMedia()
    {
        var (client, _) = Start();

        var body = await BodyAsync(await client.GetAsync($"/api/probe?path={Uri.EscapeDataString(Media.SampleVideo)}"));

        Assert.Equal("h264", body.GetProperty("video").GetProperty("codec").GetString());
        Assert.True(body.GetProperty("durationSeconds").GetDouble() > 0);
    }

    [Fact]
    public async Task ProbingSomethingThatIsNotThereIsNotFound()
    {
        var (client, _) = Start();

        var response = await client.GetAsync("/api/probe?path=/no/such/file.mkv");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProbeWithoutAPathIsABadRequest()
    {
        var (client, _) = Start();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/probe")).StatusCode);
    }

    // ---------------- encoding ----------------

    [Fact]
    public async Task EncodingSomethingThatIsNotThereIsNotFound()
    {
        var (client, _) = Start();

        var response = await client.PostAsJsonAsync("/api/encode", new { input = "/no/such/file.mkv" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EncodingWithoutAnInputIsABadRequest()
    {
        var (client, _) = Start();

        var response = await client.PostAsJsonAsync("/api/encode", new { options = new { crf = 20 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>--ffmpeg names the program the server runs: over the API that is remote code execution.</summary>
    [Fact]
    public async Task AToolPathOptionIsRefused_AndNothingIsQueued()
    {
        var (client, services) = Start();

        var response = await client.PostAsJsonAsync("/api/encode", new
        {
            input = Media.SampleVideo,
            output = Media.Output("api_refused.mkv"),
            options = new Dictionary<string, object> { ["ffmpeg"] = "/bin/sh", ["crf"] = 30 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ffmpeg", await response.Content.ReadAsStringAsync());
        Assert.Empty(services.JobQueue.Jobs);
    }

    /// <summary>
    /// The allow-list checked JSON keys, but values travelled as separate argv tokens: after a
    /// no-value flag like "overwrite" the parser read the value as an option of its own, so a
    /// forbidden option rode in through an allowed key. Every variant must be refused.
    /// </summary>
    [Theory]
    [InlineData("""{"overwrite": "--video-args=-f null"}""")]
    [InlineData("""{"overwrite": "-video-args=x"}""")]
    [InlineData("""{"chunked": "--temp-dir=/etc"}""")]
    [InlineData("""{"audio-track": ["1", "--ffmpeg=/bin/sh"]}""")]
    [InlineData("""{"crf": "22 --filter-engine vapoursynth"}""")]
    public async Task OptionsCannotBeSmuggledInsideValues(string options)
    {
        var (client, services) = Start();
        var body = $$"""{"input": {{JsonSerializer.Serialize(Media.SampleVideo)}}, "output": {{JsonSerializer.Serialize(Media.Output("api_smuggle.mkv"))}}, "options": {{options}}}""";

        var response = await client.PostAsync("/api/encode", new StringContent(body, Encoding.UTF8, "application/json"));

        // The guarantee is "refused, nothing queued". Where it's refused varies: a value that tries to
        // carry a second option is now just a bad value of the first (e.g. a CRF that isn't a number),
        // rejected when the request is built — after the input is examined, so without FFmpeg (no
        // sample to examine) the refusal is the missing input instead. Either way it's a client error.
        Assert.InRange((int)response.StatusCode, 400, 499);
        Assert.Empty(services.JobQueue.Jobs);
    }

    [Fact]
    public async Task AMalformedBodyIsABadRequestRatherThanAServerError()
    {
        var (client, _) = Start();

        var response = await client.PostAsync("/api/encode",
            new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [FfmpegFact]
    public async Task AnEncodeIsQueuedButNotStarted()
    {
        // Queueing and starting are separate so a client can submit a batch and then
        // begin, rather than each submission racing the last.
        var (client, services) = Start();

        var response = await client.PostAsJsonAsync("/api/encode", new
        {
            input = Media.SampleVideo,
            output = Media.Output("api_queued.mkv"),
            options = new { crf = 34, preset = "ultrafast" },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("queued").GetInt32() >= 2);
        Assert.False(services.JobQueue.IsRunning);

        services.QueueStore.Clear();
    }

    [FfmpegFact]
    public async Task OptionsInTheBodyReachTheEncoder()
    {
        // The body's options are turned back into a command line and parsed by the same
        // code the CLI uses. If that translation broke, an encode would silently run with
        // defaults instead of what was asked for.
        var (client, services) = Start();

        await client.PostAsJsonAsync("/api/encode", new
        {
            input = Media.SampleVideo,
            output = Media.Output("api_options.mkv"),
            // A dictionary rather than an anonymous type: option names contain hyphens.
            options = new Dictionary<string, object>
            {
                ["crf"] = 41,
                ["preset"] = "ultrafast",
                ["video-encoder"] = "ffmpeg-x264",
            },
        });

        var video = services.JobQueue.Jobs.Values.OfType<Moist.Core.Jobs.VideoJob>().Single();

        Assert.Equal("ffmpeg-x264", video.EncoderId);
        Assert.Equal(41, video.Settings.Quality);

        services.QueueStore.Clear();
    }

    [FfmpegFact]
    public async Task AQueuedEncodeRunsAndProducesItsFile()
    {
        var (client, services) = Start();
        var output = Media.Output("api_run.mkv");

        await client.PostAsJsonAsync("/api/encode", new
        {
            input = Media.SampleVideo,
            output,
            options = new { crf = 40, preset = "ultrafast" },
        });

        await client.PostAsync("/api/queue/start", null);

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline && !File.Exists(output))
            await Task.Delay(200);

        Assert.True(File.Exists(output), "the queued encode produced no file");

        await client.PostAsync("/api/queue/stop", null);
        services.QueueStore.Clear();
    }

    [FfmpegFact]
    public async Task TheQueueEndpointReportsWhatWasSubmitted()
    {
        var (client, services) = Start();

        await client.PostAsJsonAsync("/api/encode", new
        {
            input = Media.SampleVideo,
            output = Media.Output("api_listed.mkv"),
            options = new { crf = 40, preset = "ultrafast" },
        });

        var body = await BodyAsync(await client.GetAsync("/api/queue"));
        var jobs = body.GetProperty("jobs").EnumerateArray().ToList();

        Assert.NotEmpty(jobs);
        Assert.All(jobs, j => Assert.False(string.IsNullOrWhiteSpace(j.GetProperty("id").GetString())));

        services.QueueStore.Clear();
    }
}
