using Moist.Core.Encoders.Video;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Archival and professional handling against real encodes: chroma, subtitle burn-in,
/// per-track subtitle selection, SVT-AV1 tuning and HDR detection.
/// </summary>
[Collection("media")]
public sealed class Tier6IntegrationTests(MediaFixture media) : PipelineTestBase(media)
{
    private EncodeRequest Request(string input, string output) => new()
    {
        InputFile = input,
        OutputFile = Media.Output(output),
        TempDirectory = Media.TempDir,
        VideoEncoderId = "ffmpeg-x264",
        Quality = 34,
        Preset = VideoPreset.Ultrafast,
        AudioEncoderId = "ffmpeg-audio-opus",
    };

    // ---------------- chroma and bit depth ----------------

    [FfmpegTheory]
    [InlineData(ChromaSubsampling.Yuv420, VideoBitDepth.Auto, "yuv420p")]
    [InlineData(ChromaSubsampling.Yuv444, VideoBitDepth.Auto, "yuv444p")]
    [InlineData(ChromaSubsampling.Yuv422, VideoBitDepth.Ten, "yuv422p10le")]
    public async Task ChromaAndDepthReachTheOutput(
        ChromaSubsampling chroma, VideoBitDepth depth, string expectedPixelFormat)
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request(Media.SampleVideo, $"chroma_{chroma}_{depth}.mkv") with
        {
            Chroma = chroma,
            BitDepth = depth,
        };

        var jobs = EncodeJobBuilder.Build(request, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.Equal(expectedPixelFormat, Ffmpeg.PixelFormat(request.OutputFile));
    }

    [FfmpegFact]
    public async Task AskingOnlyForChromaKeepsATenBitSourceTenBit()
    {
        // Requesting 4:4:4 must not quietly throw away the source's precision.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.TenBitVideo, CancellationToken.None);

        var request = Request(Media.TenBitVideo, "chroma_keeps_depth.mkv") with
        {
            VideoEncoderId = "ffmpeg-x265",
            Chroma = ChromaSubsampling.Yuv444,
        };

        var jobs = EncodeJobBuilder.Build(request, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var pixelFormat = Ffmpeg.PixelFormat(request.OutputFile);
        Assert.Contains("444", pixelFormat);
        Assert.Contains("10", pixelFormat);
    }

    // ---------------- subtitles ----------------

    [FfmpegFact]
    public async Task BurnedInSubtitlesActuallyChangeThePicture()
    {
        // "It encoded without error" proves nothing here — the text has to be in the
        // frames. Compare against the same encode without burn-in.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);

        var burned = Request(Media.RichVideo, "burned.mkv") with { BurnInSubtitles = true };
        var plain = Request(Media.RichVideo, "not_burned.mkv");

        var burnedJobs = EncodeJobBuilder.Build(burned, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, burnedJobs), Failure(burnedJobs));

        var plainJobs = EncodeJobBuilder.Build(plain, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, plainJobs), Failure(plainJobs));

        // rich.srt shows text from 0.5s and nothing between 2.0s and 3.0s.
        var whileShowing = await FrameDifferenceAsync(burned.OutputFile, plain.OutputFile, 1.0);
        var whileBlank = await FrameDifferenceAsync(burned.OutputFile, plain.OutputFile, 2.5);

        Assert.True(whileShowing < whileBlank - 3,
            $"frames differ by {whileShowing:F1} dB where a subtitle should be and " +
            $"{whileBlank:F1} dB where there is none — burn-in doesn't appear to have rendered anything");
    }

    [FfmpegFact]
    public async Task BurnInStillLeavesASelectableSubtitleTrack()
    {
        // Burning in is about the picture; it shouldn't also strip the real track.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);

        var request = Request(Media.RichVideo, "burned_plus_track.mkv") with { BurnInSubtitles = true };
        var jobs = EncodeJobBuilder.Build(request, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.Contains("subtitle", Ffmpeg.StreamTypes(request.OutputFile));
    }

    [FfmpegFact]
    public async Task SelectingSubtitleTracksKeepsOnlyThose()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);

        Assert.NotEmpty(info.SubtitleStreams);

        var request = Request(Media.RichVideo, "no_subs_selected.mkv") with
        {
            SubtitleTracks = [99],   // out of range on purpose: nothing should be kept
        };

        var jobs = EncodeJobBuilder.Build(request, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.DoesNotContain("subtitle", Ffmpeg.StreamTypes(request.OutputFile));
    }

    [FfmpegFact]
    public async Task AnEmptySelectionKeepsEverySubtitle()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);

        var request = Request(Media.RichVideo, "all_subs.mkv");
        var jobs = EncodeJobBuilder.Build(request, info).AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.Contains("subtitle", Ffmpeg.StreamTypes(request.OutputFile));
    }

    // ---------------- SVT-AV1 tuning ----------------

    /// <summary>
    /// Only Vq is encoded for real. The tunings are passed through as numbers and which
    /// ones exist depends on the SVT-AV1 version FFmpeg was built against — Ubuntu's
    /// 6.1.1 carries SVT-AV1 1.4, which knows tune 0 and 1 and rejects the rest, so
    /// asserting that tune 5 encodes would only be asserting that the runner is new.
    /// Vq is tune 0 and has existed as long as the option has.
    ///
    /// The name-to-number mapping for every tuning is covered in the unit suite
    /// (Tier6Tests.SvtTuneNamesParse / the numbering assertions), which needs no encoder.
    /// </summary>
    [FfmpegTheory(encoder: "libsvtav1")]
    [InlineData(SvtAv1Tune.Vq)]
    public async Task SvtAv1AcceptsItsTuning(SvtAv1Tune tune)
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request(Media.SampleVideo, $"svt_{tune}.mkv") with
        {
            VideoEncoderId = "ffmpeg-svtav1",
            SvtAv1Tune = tune,
            SvtAv1Preset = 10,
            Quality = 45,
        };

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        // The tuning must actually reach the command line, not just be stored.
        var command = plan.Video!.OutputLog.First(l => l.StartsWith('['));
        Assert.Contains($"tune={(int)tune}", command);

        Assert.Equal("av1", Ffmpeg.VideoCodec(request.OutputFile));
    }

    [FfmpegFact(encoder: "libsvtav1")]
    public async Task WithoutTuningNoSvtParamsAreSent()
    {
        // Leaving it unset means leaving the encoder on its own default.
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        var request = Request(Media.SampleVideo, "svt_default.mkv") with
        {
            VideoEncoderId = "ffmpeg-svtav1",
            SvtAv1Preset = 10,
            Quality = 45,
        };

        var plan = EncodeJobBuilder.Build(request, info);
        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var command = plan.Video!.OutputLog.First(l => l.StartsWith('['));
        Assert.DoesNotContain("tune=", command);
    }

    // ---------------- HDR ----------------

    [FfmpegFact]
    public async Task HdrSourcesAreRecognised()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.TenBitVideo, CancellationToken.None);

        Assert.NotNull(info.Video);
        Assert.True(info.Video!.IsHdr, "the HDR10 fixture was not recognised as HDR");

        // Matroska doesn't necessarily surface color_transfer at container level — this
        // fixture reports it as "unknown" even though the bitstream is PQ. Detection
        // therefore cannot lean on the colour tags alone, which is why frame side data
        // is read as well.
        Assert.True(info.Video.HasHdr10Metadata, "mastering display / CLL side data was not picked up");
        Assert.Equal(10, info.Video.BitDepth);
    }

    [FfmpegFact]
    public async Task OrdinarySourcesAreNotMistakenForHdr()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);

        Assert.False(info.Video!.IsHdr);
        Assert.False(info.Video.HasDynamicHdrMetadata);
    }

    [FfmpegFact]
    public async Task ForcingEightBitOnAnHdrSourceWarnsFirst()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.TenBitVideo, CancellationToken.None);

        var warnings = EncodeJobBuilder.Warnings(
            Request(Media.TenBitVideo, "hdr8.mkv") with { BitDepth = VideoBitDepth.Eight }, info);

        Assert.Contains(warnings, w => w.Contains("HDR"));
    }

    /// <summary>
    /// PSNR between the same instant of two encodes. Lower means more different.
    ///
    /// The frames are pulled out to stills first: <c>-frames:v</c> is an output option,
    /// so trying to limit two inputs in one command doesn't parse.
    /// </summary>
    private async Task<double> FrameDifferenceAsync(string a, string b, double atSeconds)
    {
        var frameA = Media.Output($"cmp_a_{Guid.NewGuid():N}.png");
        var frameB = Media.Output($"cmp_b_{Guid.NewGuid():N}.png");

        try
        {
            await RunFfmpegAsync($"-y -v error -ss {atSeconds:F2} -i \"{a}\" -frames:v 1 \"{frameA}\"");
            await RunFfmpegAsync($"-y -v error -ss {atSeconds:F2} -i \"{b}\" -frames:v 1 \"{frameB}\"");

            var stderr = await RunFfmpegAsync(
                $"-hide_banner -i \"{frameA}\" -i \"{frameB}\" -lavfi psnr -f null -");

            var match = System.Text.RegularExpressions.Regex.Match(stderr, @"average:([\d.]+)");
            Assert.True(match.Success, $"could not measure the frames at {atSeconds}s:\n{stderr}");

            return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            try { File.Delete(frameA); } catch { }
            try { File.Delete(frameB); } catch { }
        }
    }

    private static async Task<string> RunFfmpegAsync(string args)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var stderr = await process.StandardError.ReadToEndAsync();
        _ = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stderr;
    }
}
