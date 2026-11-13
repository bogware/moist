using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Muxers;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>End-to-end encodes through the real job pipeline, verified with ffprobe.</summary>
[Collection("media")]
public sealed class PipelineTests(MediaFixture media) : PipelineTestBase(media)
{
    private EncodeRequest Request(string output, Action<EncodeRequestBuilder>? configure = null)
    {
        var b = new EncodeRequestBuilder
        {
            InputFile = Media.SampleVideo,
            OutputFile = Media.Output(output),
            TempDirectory = Media.TempDir,
        };
        configure?.Invoke(b);
        return b.Build();
    }

    [FfmpegFact]
    public async Task VideoAudioMux_ProducesBothStreams()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request("basic.mkv", b =>
        {
            b.VideoEncoderId = "ffmpeg-x265";
            b.AudioEncoderId = "ffmpeg-audio-opus";
            b.Quality = 34;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.Equal(3, jobs.Count);
        Assert.IsType<VideoJob>(jobs[0]);
        Assert.IsType<AudioJob>(jobs[1]);
        Assert.IsType<MuxJob>(jobs[2]);

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal("hevc", Ffmpeg.VideoCodec(request.OutputFile));
        Assert.Equal("opus", Ffmpeg.AudioCodec(request.OutputFile));
    }

    [FfmpegFact]
    public async Task AudioOnly_SkipsVideoAndMux()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request("audio.opus", b =>
        {
            b.AudioOnly = true;
            b.AudioEncoderId = "ffmpeg-audio-opus";
            b.AudioNormalization = AudioNormalizationMode.EbuR128;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.Single(jobs);
        Assert.Null(plan.Video);
        Assert.Null(plan.Mux);

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal("opus", Ffmpeg.AudioCodec(request.OutputFile));
        Assert.Empty(Ffmpeg.VideoCodec(request.OutputFile));
    }

    [FfmpegTheory]
    [InlineData("ffmpeg-x264", "h264")]
    [InlineData("ffmpeg-x265", "hevc")]
    [InlineData("ffmpeg-vp9", "vp9")]
    [InlineData("ffmpeg-ffv1", "ffv1")]
    public async Task EveryVideoEncoder_ProducesItsCodec(string encoderId, string expectedCodec)
        => await EncoderProducesCodec(encoderId, expectedCodec);

    /// <summary>
    /// AV1 is its own test because libsvtav1 is a build-time component most FFmpeg
    /// packages leave out.
    /// </summary>
    [FfmpegFact(encoder: "libsvtav1")]
    public async Task SvtAv1ProducesAv1() => await EncoderProducesCodec("ffmpeg-svtav1", "av1");

    private async Task EncoderProducesCodec(string encoderId, string expectedCodec)
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request($"v_{encoderId}.mkv", b =>
        {
            b.VideoEncoderId = encoderId;
            b.Quality = 36;
            b.AudioEncoderId = "ffmpeg-audio-opus";
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal(expectedCodec, Ffmpeg.VideoCodec(request.OutputFile));
    }

    [FfmpegTheory]
    [InlineData("ffmpeg-audio-opus", "opus", ".opus")]
    [InlineData("ffmpeg-audio-aac", "aac", ".m4a")]
    [InlineData("ffmpeg-audio-mp3", "mp3", ".mp3")]
    [InlineData("ffmpeg-audio-flac", "flac", ".flac")]
    [InlineData("ffmpeg-audio-ac3", "ac3", ".ac3")]
    [InlineData("ffmpeg-audio-eac3", "eac3", ".eac3")]
    [InlineData("ffmpeg-audio-vorbis", "vorbis", ".ogg")]
    public async Task EveryAudioEncoder_ProducesItsCodec(string encoderId, string expectedCodec, string extension)
    {
        Assert.Equal(extension, EncodeJobBuilder.AudioFileExtension(encoderId));

        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request($"a_{encoderId}{extension}", b =>
        {
            b.AudioOnly = true;
            b.AudioEncoderId = encoderId;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal(expectedCodec, Ffmpeg.AudioCodec(request.OutputFile));
    }

    [FfmpegTheory]
    [InlineData(ContainerFormat.Mkv, "ffmpeg-x264", "ffmpeg-audio-aac")]
    [InlineData(ContainerFormat.Mp4, "ffmpeg-x264", "ffmpeg-audio-aac")]
    [InlineData(ContainerFormat.Mov, "ffmpeg-x264", "ffmpeg-audio-aac")]
    [InlineData(ContainerFormat.Ts, "ffmpeg-x264", "ffmpeg-audio-aac")]
    [InlineData(ContainerFormat.Avi, "ffmpeg-x264", "ffmpeg-audio-mp3")]
    [InlineData(ContainerFormat.Webm, "ffmpeg-vp9", "ffmpeg-audio-vorbis")]
    public async Task EveryContainer_MuxesSuccessfully(ContainerFormat container, string video, string audio)
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request($"c_{container}{container.GetExtension()}", b =>
        {
            b.Container = container;
            b.VideoEncoderId = video;
            b.AudioEncoderId = audio;
            b.Quality = 36;
        });

        // Every combination in this theory must pass its own validator.
        Assert.Null(EncodeJobBuilder.Validate(request));

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Contains("video", Ffmpeg.StreamTypes(request.OutputFile));
        Assert.Contains("audio", Ffmpeg.StreamTypes(request.OutputFile));
    }

    [FfmpegFact]
    public async Task ExternalSubtitle_IsMuxedIn()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request("subs.mkv", b =>
        {
            b.VideoEncoderId = "ffmpeg-x264";
            b.AudioEncoderId = "ffmpeg-audio-aac";
            b.Quality = 36;
            b.ExternalSubtitleFile = Media.SubtitleFile;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Contains("subtitle", Ffmpeg.StreamTypes(request.OutputFile));
    }

    [FfmpegFact]
    public async Task FilterChain_ChangesTheResolution()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var engine = services.Filters.Engines.Single(e => e.Id == "ffmpeg");
        var resize = engine.GetAvailableFilters().Single(f => f.Id == "resize");

        var chain = new Moist.Filters.FilterChain();
        chain.Add(resize, new Dictionary<string, object> { ["width"] = 160, ["height"] = 120 });
        var script = engine.GenerateScript(chain, Media.SampleVideo);

        Assert.Contains("scale=160:120", script);

        var request = Request("filtered.mkv", b =>
        {
            b.VideoEncoderId = "ffmpeg-x264";
            b.AudioEncoderId = "ffmpeg-audio-aac";
            b.Quality = 36;
            b.FilterScript = script;
            b.FilterEngineId = engine.Id;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal((160, 120), Ffmpeg.Resolution(request.OutputFile));
    }

    [FfmpegFact]
    public async Task Cancelling_StopsTheJobAndKillsFfmpeg()
    {
        using var services = CreateServices(concurrency: 2);
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request("cancelled.mkv", b =>
        {
            b.VideoEncoderId = "ffmpeg-x265";
            b.Preset = VideoPreset.Veryslow;   // slow enough to still be running when we cancel
            b.Quality = 12;
            b.AudioEncoderId = "ffmpeg-audio-opus";
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();
        foreach (var job in jobs) services.JobQueue.Enqueue(job);

        var run = services.JobQueue.StartAsync();
        var video = plan.Video!;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (video.Status != JobStatus.Running && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(JobStatus.Running, video.Status);

        services.JobQueue.CancelJob(video.Id);

        deadline = DateTime.UtcNow.AddSeconds(20);
        while (video.Status == JobStatus.Running && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        services.JobQueue.Stop();
        try { await run.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }

        Assert.Equal(JobStatus.Cancelled, video.Status);
    }

    [FfmpegFact]
    public async Task ConcurrentEncodes_AllSucceed()
    {
        using var services = CreateServices(concurrency: 4);
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var all = new List<Job>();
        for (var i = 0; i < 3; i++)
        {
            var request = Request($"conc_{i}.mkv", b =>
            {
                b.VideoEncoderId = "ffmpeg-x264";
                b.AudioEncoderId = "ffmpeg-audio-opus";
                b.Quality = 36;
            });
            all.AddRange(EncodeJobBuilder.Build(request, info).AllJobs);
        }

        Assert.Equal(9, all.Count);
        Assert.True(await RunAsync(services, all), Failure(all));

        for (var i = 0; i < 3; i++)
            Assert.True(File.Exists(Media.Output($"conc_{i}.mkv")), $"conc_{i}.mkv missing");
    }

    [FfmpegTheory]
    [InlineData("my holiday video.mkv")]
    [InlineData("日本語のビデオ.mkv")]
    [InlineData("o'brien video.mkv")]
    [InlineData("rock & roll.mkv")]
    [InlineData("video [1080p] (final).mkv")]
    [InlineData("100%_done.mkv")]
    [InlineData("a,b=c.mkv")]
    public async Task AwkwardFilenames_StillEncode(string fileName)
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request(fileName, b =>
        {
            b.VideoEncoderId = "ffmpeg-x264";
            b.AudioEncoderId = "ffmpeg-audio-aac";
            b.Quality = 38;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.True(File.Exists(request.OutputFile));
    }

    /// <summary>
    /// Cleanup used to delete any directory it left empty — including the temp directory itself.
    /// A batch builds every job up front, so when the first finished in a dedicated (otherwise
    /// empty) temp folder, the folder vanished and the next job wrote into nothing.
    /// </summary>
    [FfmpegFact]
    public async Task CleanupNeverDeletesTheTempDirectoryItself()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var dedicated = Path.Combine(Media.Root, "dedicated_tmp_" + Guid.NewGuid().ToString("N")[..6]);

        EncodeRequest Req(string name) => new EncodeRequestBuilder
        {
            InputFile = Media.SampleVideo, OutputFile = Media.Output(name), TempDirectory = dedicated,
            VideoEncoderId = "ffmpeg-x264", AudioEncoderId = "ffmpeg-audio-aac", Quality = 40,
        }.Build();

        // Both built before either runs — exactly what a batch does.
        var jobs = EncodeJobBuilder.Build(Req("batch_a.mkv"), info).AllJobs
            .Concat(EncodeJobBuilder.Build(Req("batch_b.mkv"), info).AllJobs).ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.True(File.Exists(Media.Output("batch_b.mkv")));
        Assert.True(Directory.Exists(dedicated), "the temp directory was deleted by cleanup");
    }

    [FfmpegFact]
    public async Task MissingTool_FailsWithAnActionableMessage()
    {
        using var services = CreateServices();
        services.Settings.FFmpegPath = "ffmpeg-definitely-not-installed";

        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var request = Request("missing.mkv", b => b.VideoEncoderId = "ffmpeg-x264");

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();

        Assert.False(await RunAsync(services, jobs, TimeSpan.FromSeconds(60)));

        var failed = jobs.First(j => j.Status == JobStatus.Failed);
        Assert.Contains("not found", failed.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [FfmpegFact]
    public async Task TempFiles_AreRemovedOnSuccess()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request("cleanup.mkv", b =>
        {
            b.VideoEncoderId = "ffmpeg-x264";
            b.AudioEncoderId = "ffmpeg-audio-aac";
            b.Quality = 38;
        });

        var plan = EncodeJobBuilder.Build(request, info);
        var temps = plan.Mux!.FilesToCleanup.ToList();
        var jobs = plan.AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        foreach (var temp in temps)
            Assert.False(File.Exists(temp), $"temp file left behind: {temp}");
    }
}

/// <summary>Mutable builder so tests can tweak a few fields of the init-only request.</summary>
public sealed class EncodeRequestBuilder
{
    public required string InputFile { get; init; }
    public required string OutputFile { get; init; }
    public required string TempDirectory { get; init; }

    public string VideoEncoderId { get; set; } = "ffmpeg-x264";
    public string AudioEncoderId { get; set; } = "ffmpeg-audio-opus";
    public VideoEncodingMode Mode { get; set; } = VideoEncodingMode.ConstantRateFactor;
    public double Quality { get; set; } = 34;
    public int Bitrate { get; set; } = 1000;
    public VideoPreset Preset { get; set; } = VideoPreset.Ultrafast;
    public ContainerFormat Container { get; set; } = ContainerFormat.Mkv;
    public bool AudioOnly { get; set; }
    public AudioNormalizationMode AudioNormalization { get; set; } = AudioNormalizationMode.None;
    public string? ExternalSubtitleFile { get; set; }
    public string? FilterScript { get; set; }
    public string? FilterEngineId { get; set; }
    public X265StreamWrap X265StreamWrap { get; set; } = X265StreamWrap.NoBFrames;

    public EncodeRequest Build() => new()
    {
        InputFile = InputFile,
        OutputFile = OutputFile,
        TempDirectory = TempDirectory,
        VideoEncoderId = VideoEncoderId,
        AudioEncoderId = AudioEncoderId,
        Mode = Mode,
        Quality = Quality,
        Bitrate = Bitrate,
        Preset = Preset,
        Container = Container,
        AudioOnly = AudioOnly,
        AudioNormalization = AudioNormalization,
        ExternalSubtitleFile = ExternalSubtitleFile,
        FilterScript = FilterScript,
        FilterEngineId = FilterEngineId,
        X265StreamWrap = X265StreamWrap,
        AudioBitrate = 96,
    };
}
