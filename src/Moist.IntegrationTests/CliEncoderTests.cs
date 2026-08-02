using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// The standalone x264 and x265 encoders.
///
/// These shipped untested for a long time and did not work at all: every encode failed
/// with an empty error message, because the pipeline moist built was mangled by the shell
/// before either tool saw it. Everything here is a regression test for something that was
/// actually broken, so the assertions are about the file on disk rather than exit codes —
/// several of these failures produced a "successful" encode with the wrong content.
/// </summary>
[Collection("media")]
public sealed class CliEncoderTests(MediaFixture media) : PipelineTestBase(media)
{
    private AppServices CreateCliServices()
    {
        var services = CreateServices();
        if (CliEncoder.X264 is { } x264) services.Settings.X264Path = x264;
        if (CliEncoder.X265 is { } x265) services.Settings.X265Path = x265;
        return services;
    }

    private async Task<string> EncodeAsync(AppServices services, string encoderId, string input, string output,
        Action<EncodeRequestBuilder>? tweak = null)
    {
        var builder = new EncodeRequestBuilder
        {
            InputFile = input,
            OutputFile = Media.Output(output),
            TempDirectory = Media.TempDir,
        };
        builder.VideoEncoderId = encoderId;
        builder.Quality = 30;
        tweak?.Invoke(builder);

        var request = builder.Build();
        var info = await services.MediaAnalyzer.AnalyzeAsync(input, CancellationToken.None);
        var jobs = EncodeJobBuilder.Build(request, info).AllJobs.ToList();

        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.True(File.Exists(request.OutputFile), $"{encoderId} reported success but wrote no file");

        return request.OutputFile;
    }

    // ---------------- x264 ----------------

    [X264Fact]
    public async Task X264EncodesThroughThePipe()
    {
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x264", Media.SampleVideo, "cli_x264.mkv");

        Assert.Equal("h264", Ffmpeg.VideoCodec(output));
        Assert.Equal(Ffmpeg.FrameCount(Media.SampleVideo), Ffmpeg.FrameCount(output));
    }

    [X264Fact]
    public async Task X264KeepsTenBitSourcesAtTenBit()
    {
        // x264 defaults its output to 8-bit no matter what the pipe carries, so this
        // silently threw away a 10-bit master's precision while reporting success.
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x264", Media.TenBitVideo, "cli_x264_10bit.mkv");

        Assert.Equal("yuv420p10le", Ffmpeg.PixelFormat(output));
    }

    [X264Fact]
    public async Task X264HandlesFilenamesTheShellWouldOtherwiseEat()
    {
        // The pipeline goes through cmd.exe or sh, so a path is exposed to shell parsing
        // twice over. '&' separates commands, and spaces split arguments.
        using var services = CreateCliServices();
        var awkward = Path.Combine(Media.Root, "a & b (côpy) 入力.mp4");
        File.Copy(Media.SampleVideo, awkward, overwrite: true);

        var output = await EncodeAsync(services, "cli-x264", awkward, "cli x264 & out.mkv");

        Assert.Equal("h264", Ffmpeg.VideoCodec(output));
        Assert.Equal(Ffmpeg.FrameCount(awkward), Ffmpeg.FrameCount(output));
    }

    [X264Fact]
    public async Task X264EncodesOnlyTheFramesItWasAskedFor()
    {
        // Chunked encoding, target-quality search and per-scene optimisation all work by
        // encoding pieces of the source. The CLI encoders ignored the trim entirely, so
        // every chunk and every probe quietly encoded the whole file instead.
        using var services = CreateCliServices();

        var request = new EncodeRequestBuilder
        {
            InputFile = Media.SampleVideo,
            OutputFile = Media.Output("cli_x264_trimmed.mkv"),
            TempDirectory = Media.TempDir,
        };
        request.VideoEncoderId = "cli-x264";

        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var plan = EncodeJobBuilder.Build(request.Build(), info);

        var videoJob = Assert.IsType<VideoJob>(plan.Video);
        videoJob.TrimFrames = 20;

        // Just the video job: the mux would pair 20 frames with full-length audio.
        Assert.True(await RunAsync(services, [videoJob]), Failure([videoJob]));

        Assert.Equal(20, Ffmpeg.FrameCount(videoJob.OutputFile));
    }

    [X264Fact]
    public async Task X264AppliesTheSettingsItIsGiven()
    {
        // The arguments that were logged and the arguments that ran were built by two
        // different methods which had drifted apart, so the x264-specific settings —
        // reference frames, B-frames, CABAC — were dropped on the way to the encoder.
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x264", Media.SampleVideo, "cli_x264_settings.mkv",
            b => b.Quality = 40);

        var job = new VideoJob
        {
            Name = "probe",
            InputFile = Media.SampleVideo,
            OutputFile = output,
            EncoderId = "cli-x264",
            Settings = new X264Settings { RefFrames = 5, BFrames = 4, Cabac = false, Quality = 40 },
        };

        var arguments = new ArgumentCapturingX264(CliEncoder.X264!, "ffmpeg").Arguments(job);

        Assert.Contains("--ref 5", arguments);
        Assert.Contains("--bframes 4", arguments);
        Assert.Contains("--no-cabac", arguments);
    }

    // ---------------- x265 ----------------

    [X265Fact]
    public async Task X265EncodesThroughThePipe()
    {
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x265", Media.SampleVideo, "cli_x265.mkv");

        Assert.Equal("hevc", Ffmpeg.VideoCodec(output));
        Assert.Equal(Ffmpeg.FrameCount(Media.SampleVideo), Ffmpeg.FrameCount(output));
    }

    [X265Fact]
    public async Task X265KeepsTheSourceFrameRateExactly()
    {
        // x265 writes a raw elementary stream with no timing in it at all. Wrapped
        // carelessly the result plays at FFmpeg's assumed 25 fps, which changes the
        // duration and slides the picture out of sync with its audio.
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x265", Media.NtscVideo, "cli_x265_ntsc.mkv");

        Assert.Equal("24000/1001", Ffmpeg.FrameRate(output));
        Assert.Equal(Ffmpeg.FrameCount(Media.NtscVideo), Ffmpeg.FrameCount(output));

        // One decimal is loose enough to ignore how containers round their own duration,
        // and tight enough to catch the failure this guards: the same frames timed at an
        // assumed 25 fps come out ~4% short.
        Assert.Equal(Ffmpeg.Duration(Media.NtscVideo), Ffmpeg.Duration(output), 1);
    }

    [X265Fact]
    public async Task X265KeepsTenBitSourcesAtTenBit()
    {
        // A multilib x265 encodes at its own default depth regardless of the input.
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x265", Media.TenBitVideo, "cli_x265_10bit.mkv");

        Assert.Equal("yuv420p10le", Ffmpeg.PixelFormat(output));
    }

    [X265Fact]
    public async Task X265LeavesNoRawBitstreamBehind()
    {
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x265", Media.SampleVideo, "cli_x265_clean.mkv");

        Assert.Empty(Directory.GetFiles(Media.TempDir, "*.hevc"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.hevc"));
    }

    /// <summary>Exposes the argument builder so a test can read what x264 is actually sent.</summary>
    private sealed class ArgumentCapturingX264(string x264Path, string ffmpegPath)
        : X264CliProcessor(x264Path, ffmpegPath)
    {
        public string Arguments(Job job) => BuildArguments(job);
    }
}
