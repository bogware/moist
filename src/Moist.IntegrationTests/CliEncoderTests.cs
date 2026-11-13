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

    [X264Fact]
    public async Task X264TwoPassProducesTheWholeClip()
    {
        // Standalone-CLI two-pass has its own path: TwoPassProcessor injects --pass/--stats
        // through CustomArgs into the shell pipeline, and pass 1 writes to the null device.
        // Only ffmpeg-x264 two-pass was ever covered, so this whole path shipped untested.
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x264", Media.SampleVideo, "cli_x264_2pass.mkv",
            b => { b.Mode = VideoEncodingMode.TwoPassBitrate; b.Bitrate = 500; });

        Assert.Equal("h264", Ffmpeg.VideoCodec(output));
        Assert.Equal(Ffmpeg.FrameCount(Media.SampleVideo), Ffmpeg.FrameCount(output));
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

        // FFmpeg before 7 (Debian 12's 5.1, Ubuntu 24.04's 6.1) duplicates the final frame when
        // it converts to the constant rate a y4m pipe requires: one repeated 42 ms frame at the very
        // end. CFR conversion is still right — it is what keeps a variable-rate source in sync once
        // x265's untimed stream is wrapped at the nominal rate — and chunks/trims are capped by
        // -frames:v, so only a whole-file encode on an old FFmpeg sees it. Exact on 7+.
        var expected = Ffmpeg.FrameCount(Media.NtscVideo);
        if (Ffmpeg.MajorVersion >= 7) Assert.Equal(expected, Ffmpeg.FrameCount(output));
        else Assert.InRange(Ffmpeg.FrameCount(output), expected, expected + 1);

        // Within 60 ms: one frame (FFmpeg < 7's trailing CFR duplicate, above) plus container
        // rounding — and still far tighter than the failure this guards: the same frames timed at an
        // assumed 25 fps come out ~4% (130 ms here) short. Not "to one decimal place": 3.023 vs
        // 3.067 straddles a rounding boundary and failed although they differ by a single frame.
        AssertDurationMatches(Ffmpeg.Duration(Media.NtscVideo), Ffmpeg.Duration(output));
    }

    /// <summary>
    /// The bug the frame-count and duration checks above could not see: with B-frames, the
    /// FFmpeg wrap numbered packets in decode order, so the presentation timestamps ran
    /// 0, .042, .167, .125, .209, .083… — right frames, wrong times. Every wrap mode must give
    /// strictly increasing, evenly spaced presentation timestamps.
    /// </summary>
    [X265Fact]
    public async Task X265_DefaultWrap_GivesMonotonicEvenlySpacedTimestamps()
    {
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x265", Media.NtscVideo, "cli_x265_order.mkv");
        AssertMonotonicEven(output);
    }

    [X265Fact]
    public async Task X265_MkvmergeWrap_KeepsBFrames_WithCorrectTimestamps()
    {
        var mkvmerge = FindMkvmerge();
        if (mkvmerge is null) return;   // MKVToolNix not installed here: nothing to prove
        using var services = CreateCliServices();
        services.Settings.MkvMergePath = mkvmerge;
        var output = await EncodeAsync(services, "cli-x265", Media.NtscVideo, "cli_x265_mkvmerge.mkv",
            b => b.X265StreamWrap = X265StreamWrap.Mkvmerge);
        AssertMonotonicEven(output);
        AssertDurationMatches(Ffmpeg.Duration(Media.NtscVideo), Ffmpeg.Duration(output));
    }

    private static void AssertDurationMatches(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 0.060, $"duration {actual:F3}s vs source {expected:F3}s");

    private static void AssertMonotonicEven(string file)
    {
        var pts = Ffmpeg.Probe("-v error -select_streams v:0 -show_entries frame=pts_time -of csv=p=0", file)
            .Split('\n').Select(l => l.Trim().Trim(',')).Where(l => l.Length > 0)
            .Select(l => double.Parse(l, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.True(pts.Count > 10, $"only {pts.Count} frames probed");
        for (var i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] > pts[i - 1], $"timestamps go backwards at frame {i}: {pts[i - 1]:F3} -> {pts[i]:F3}");
            Assert.InRange(pts[i] - pts[i - 1], 0.035, 0.050);   // 24000/1001 ≈ 41.7 ms (Matroska ms rounding)
        }
    }

    private static string? FindMkvmerge()
    {
        foreach (var candidate in new[] { "mkvmerge", @"C:\Program Files\MKVToolNix\mkvmerge.exe" })
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(candidate, "--version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (p is null) continue;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(10_000);
                if (p.ExitCode == 0) return candidate;
            }
            catch { /* not here */ }
        }
        return null;
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

    [X265Fact]
    public async Task X265TwoPassProducesTheWholeClip()
    {
        // The raw-stream wrap in OnAfterSuccessAsync runs after every successful run, including
        // pass 1, whose output is the null device. Before the fix it threw ("wrote no bitstream"
        // / "Unable to choose an output format") and the whole two-pass job failed after the full
        // first-pass encode. This exercises pass 1 -> null-device wrap skip -> pass 2 -> real wrap.
        using var services = CreateCliServices();
        var output = await EncodeAsync(services, "cli-x265", Media.SampleVideo, "cli_x265_2pass.mkv",
            b => { b.Mode = VideoEncodingMode.TwoPassBitrate; b.Bitrate = 500; });

        Assert.Equal("hevc", Ffmpeg.VideoCodec(output));
        Assert.Equal(Ffmpeg.FrameCount(Media.SampleVideo), Ffmpeg.FrameCount(output));
    }

    /// <summary>Exposes the argument builder so a test can read what x264 is actually sent.</summary>
    private sealed class ArgumentCapturingX264(string x264Path, string ffmpegPath)
        : X264CliProcessor(x264Path, ffmpegPath)
    {
        public string Arguments(Job job) => BuildArguments(job);
    }
}
