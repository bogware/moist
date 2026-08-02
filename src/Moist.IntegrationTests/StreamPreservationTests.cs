using System.Diagnostics;
using Moist.Core.Encoders.Video;
using Moist.Core.Muxers;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Everything that has to survive a round trip: bit depth, extra audio tracks,
/// chapters, attachments and HDR signalling. Each of these was silently lost or
/// outright fatal before.
/// </summary>
[Collection("media")]
public sealed class StreamPreservationTests(MediaFixture media) : PipelineTestBase(media)
{
    // ---------------------------------------------------------------------
    // 10-bit input used to fail outright: x264/x265 were handed hardcoded
    // 8-bit-only profiles ("high"/"main"), which the encoder rejects with
    // "profile doesn't support a bit depth of 10".
    // ---------------------------------------------------------------------

    [FfmpegTheory]
    [InlineData("ffmpeg-x265", "hevc")]
    [InlineData("ffmpeg-x264", "h264")]
    public async Task TenBitSource_EncodesAndStaysTenBit(string encoderId, string expectedCodec)
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.TenBitVideo, CancellationToken.None);

        Assert.Equal(10, info.Video!.BitDepth);

        var output = Media.Output($"tenbit_{encoderId}.mkv");
        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.TenBitVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = encoderId,
            Quality = 34,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.Equal(expectedCodec, Ffmpeg.VideoCodec(output));
        Assert.Contains("10", Ffmpeg.PixelFormat(output));
    }

    [FfmpegFact]
    public async Task BitDepthCanBeForcedDown()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.TenBitVideo, CancellationToken.None);
        var output = Media.Output("forced8.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.TenBitVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x265",
            Quality = 34,
            Preset = VideoPreset.Ultrafast,
            BitDepth = VideoBitDepth.Eight,
            AudioEncoderId = "ffmpeg-audio-opus",
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));
        Assert.Equal("yuv420p", Ffmpeg.PixelFormat(output));
    }

    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("yuv420p10le", 10)]
    [InlineData("yuv422p10le", 10)]
    [InlineData("yuv444p12le", 12)]
    [InlineData("p010le", 10)]
    [InlineData("gray10le", 10)]
    [InlineData("nv12", 8)]      // chroma layout, not depth
    [InlineData("yuyv422", 8)]   // likewise
    [InlineData("", 8)]
    public void BitDepthIsDerivedFromPixelFormat(string pixelFormat, int expected)
        => Assert.Equal(expected, Moist.Core.Media.FFprobeMediaAnalyzer.BitDepthFromPixelFormat(pixelFormat));

    [FfmpegFact]
    public async Task HdrSignallingSurvivesReEncode()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.TenBitVideo, CancellationToken.None);
        var output = Media.Output("hdr.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.TenBitVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x265",
            Quality = 34,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var colour = Ffmpeg.Probe(
            "-v error -select_streams v:0 -show_entries stream=color_primaries,color_transfer,color_space -of default=nw=1",
            output);

        Assert.Contains("bt2020", colour);
        Assert.Contains("smpte2084", colour);
    }

    // ---------------------------------------------------------------------
    // Only one audio track was ever encoded, so multi-language sources lost
    // everything but the selected track.
    // ---------------------------------------------------------------------

    [FfmpegFact]
    public async Task AllAudioTracksAreEncoded_WithLanguageAndTitle()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);

        Assert.Equal(3, info.AudioStreams.Count);

        var output = Media.Output("multitrack.mkv");
        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.RichVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Quality = 38,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioTracks = [0, 1, 2],
        }, info);

        // One video job, three audio jobs, one mux.
        Assert.Equal(3, plan.AudioJobs.Count);
        Assert.Equal(5, plan.AllJobs.Count());

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var streams = Ffmpeg.Probe("-v error -select_streams a -show_entries stream=codec_name:stream_tags=language,title -of default=nw=1", output);

        Assert.Equal(3, streams.Split("codec_name=").Length - 1);
        Assert.Contains("eng", streams);
        Assert.Contains("jpn", streams);
        Assert.Contains("fra", streams);
        Assert.Contains("Commentary", streams);
    }

    [FfmpegFact]
    public async Task SelectingASubsetKeepsOnlyThoseTracks()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);
        var output = Media.Output("subset.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.RichVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Quality = 38,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioTracks = [2],
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var streams = Ffmpeg.Probe("-v error -select_streams a -show_entries stream_tags=language -of default=nw=1", output);
        Assert.Contains("fra", streams);
        Assert.DoesNotContain("jpn", streams);
    }

    // ---------------------------------------------------------------------
    // Chapters and attachments live only in the source container, and the mux
    // reads from temp files, so both were silently dropped.
    // ---------------------------------------------------------------------

    [FfmpegFact]
    public async Task ChaptersAndAttachmentsSurvive()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);
        var output = Media.Output("chapters.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.RichVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Quality = 38,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var chapters = Ffmpeg.Probe("-v error -show_chapters -of default=nw=1", output);
        Assert.Contains("Opening", chapters);
        Assert.Contains("Middle", chapters);
        Assert.Contains("Ending", chapters);

        Assert.Contains("attachment", Ffmpeg.StreamTypes(output));
    }

    [FfmpegFact]
    public async Task ChaptersCanBeDeclined()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.RichVideo, CancellationToken.None);
        var output = Media.Output("nochapters.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.RichVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Quality = 38,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-opus",
            CopyChapters = false,
            CopyAttachments = false,
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.DoesNotContain("Opening", Ffmpeg.Probe("-v error -show_chapters -of default=nw=1", output));
        Assert.DoesNotContain("attachment", Ffmpeg.StreamTypes(output));
    }

    // ---------------------------------------------------------------------
    // Hardware encoding. Skips when no usable GPU encoder is present.
    // ---------------------------------------------------------------------

    [HardwareFact]
    public async Task HardwareEncoder_ProducesAPlayableFile()
    {
        using var services = CreateServices();
        var encoderId = await HardwareProbe.FirstUsableAsync(services);
        Assert.NotNull(encoderId);

        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var output = Media.Output("hardware.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.SampleVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = encoderId!,
            Quality = 30,
            Preset = VideoPreset.Fast,
            AudioEncoderId = "ffmpeg-audio-opus",
        }, info);

        // The plan must carry hardware settings, or two-pass would try to wrap it.
        Assert.IsType<HardwareVideoSettings>(plan.Video!.Settings);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        var expectedCodec = ContainerCompatibility.StreamCodecForEncoder(encoderId);
        Assert.Equal(expectedCodec, Ffmpeg.VideoCodec(output));
    }

    [HardwareFact]
    public async Task HardwareTwoPass_RunsAsASinglePassWithAnalysis()
    {
        using var services = CreateServices();
        var encoderId = await HardwareProbe.FirstUsableAsync(services);
        Assert.NotNull(encoderId);

        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var output = Media.Output("hw_twopass.mkv");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.SampleVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = encoderId!,
            Mode = VideoEncodingMode.TwoPassBitrate,
            Bitrate = 2000,
            Preset = VideoPreset.Fast,
            AudioEncoderId = "ffmpeg-audio-opus",
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        // The point of the test: no -pass 1 / -pass 2 pair. Hardware encoders reject
        // those flags and do their analysis inside a single run instead.
        var log = string.Join("\n", plan.Video!.OutputLog);
        Assert.DoesNotContain("-pass 1", log);
        Assert.DoesNotContain("-pass 2", log);
        Assert.True(File.Exists(output));
    }

    [FfmpegFact]
    public async Task DetectionIsHonestAboutWhatIsMissing()
    {
        using var services = CreateServices();
        var statuses = await services.DetectHardwareAsync();

        Assert.NotEmpty(statuses);

        // Anything reported unusable must say why — a silent "no" is not actionable.
        foreach (var status in statuses.Where(s => !s.Usable))
            Assert.False(string.IsNullOrWhiteSpace(status.Reason), $"{status.Encoder.Id} gave no reason");

        // And anything usable must not carry a failure reason.
        foreach (var status in statuses.Where(s => s.Usable))
            Assert.Null(status.Reason);
    }
}

/// <summary>Runs a test only when at least one GPU encoder actually works here.</summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (Ffmpeg.SkipReason is { } reason) { Skip = reason; return; }
        if (!HardwareProbe.AnyUsable) Skip = "No usable hardware encoder on this machine.";
    }
}

/// <summary>Caches a one-off hardware probe so each test doesn't re-run it.</summary>
public static class HardwareProbe
{
    private static readonly Lazy<IReadOnlyList<HardwareEncoderStatus>> _statuses = new(() =>
    {
        if (!Ffmpeg.Available) return [];
        try
        {
            var capabilities = new HardwareCapabilities();
            return capabilities.DetectAsync("ffmpeg").GetAwaiter().GetResult();
        }
        catch
        {
            return [];
        }
    });

    public static bool AnyUsable => _statuses.Value.Any(s => s.Usable);

    public static async Task<string?> FirstUsableAsync(AppServices services)
    {
        var statuses = await services.DetectHardwareAsync();
        return statuses.FirstOrDefault(s => s.Usable)?.Encoder.Id;
    }
}
