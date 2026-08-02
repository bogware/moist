using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Muxers;
using Moist.Core.Settings;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Each test here pins a bug that shipped. They exist because the unit suite never
/// launched FFmpeg or wrote a settings file, so all three survived it.
/// </summary>
[Collection("media")]
public sealed class RegressionTests(MediaFixture media) : PipelineTestBase(media)
{
    // ---------------------------------------------------------------------
    // Settings could not be saved at all: WindowX/WindowY defaulted to
    // double.NaN, which System.Text.Json refuses to serialize. Every call to
    // Save() threw, and the unguarded call in MainWindow.OnClosing left the
    // process hung on exit.
    // ---------------------------------------------------------------------

    [Fact]
    public void Save_DoesNotThrow_WhenWindowPositionWasNeverSet()
    {
        using var _ = new SettingsSandbox();

        var settings = new MoistSettings();
        Assert.Null(settings.WindowX);
        Assert.Null(settings.WindowY);

        var saved = settings.Save(out var error);

        Assert.True(saved, $"a freshly constructed settings object must be saveable, got: {error}");
        Assert.Null(error);
    }

    [Fact]
    public void Save_SurvivesNonFiniteWindowGeometry()
    {
        using var _ = new SettingsSandbox();

        // The UI toolkit can hand back NaN/Infinity for window metrics.
        var settings = new MoistSettings
        {
            WindowWidth = double.NaN,
            WindowHeight = double.PositiveInfinity,
            WindowX = double.NaN,
            WindowY = double.NegativeInfinity,
        };

        Assert.True(settings.Save(out var error), $"save must sanitize non-finite values, got: {error}");
        Assert.Null(error);

        var reloaded = MoistSettings.Load();
        Assert.True(double.IsFinite(reloaded.WindowWidth));
        Assert.True(double.IsFinite(reloaded.WindowHeight));
        Assert.Null(reloaded.WindowX);
        Assert.Null(reloaded.WindowY);
    }

    [Fact]
    public void Save_ReportsFailureInsteadOfThrowing_WhenTheFileCannotBeWritten()
    {
        // Save() runs on the shutdown path, where throwing leaves the window
        // un-closable and the process hung. It must degrade to a false return.
        using var sandbox = new SettingsSandbox();

        var path = MoistSettings.GetSettingsPath();
        if (File.Exists(path)) File.Delete(path);

        // A directory where the file should be. This used to hold the file open with
        // FileShare.None instead, which only blocks a write on Windows — Unix file locks
        // are advisory, so on Linux the save succeeded and the test failed. A directory
        // in the way cannot be written on any platform.
        Directory.CreateDirectory(path);

        try
        {
            var settings = new MoistSettings();

            var exception = Record.Exception(() => settings.Save(out _));
            Assert.Null(exception);

            var saved = settings.Save(out var error);
            Assert.False(saved, "an unwritable settings path must be reported as a failure");
            Assert.NotNull(error);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void Load_RepairsAnUnusableTempDirectory()
    {
        // A temp directory that can't be created makes every encode fail, and the bad
        // value is written straight back on the next save. Loading must heal it.
        using var sandbox = new SettingsSandbox();

        var settings = new MoistSettings { TempDirectory = "\0invalid\0" };
        settings.Save(out _);

        var reloaded = MoistSettings.Load();

        Assert.Equal(MoistSettings.DefaultTempDirectory, reloaded.TempDirectory);
        Assert.DoesNotContain('\0', reloaded.TempDirectory);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        using var _ = new SettingsSandbox();

        var settings = MoistSettings.Load();
        settings.MaxConcurrentJobs = 7;
        settings.AutoStartQueue = true;
        settings.DefaultOutputDirectory = @"C:\some dir\ünïcødé";
        settings.WindowX = 120;
        settings.WindowY = 80;

        Assert.True(settings.Save(out var error), error);

        var reloaded = MoistSettings.Load();
        Assert.Equal(7, reloaded.MaxConcurrentJobs);
        Assert.True(reloaded.AutoStartQueue);
        Assert.Equal(@"C:\some dir\ünïcødé", reloaded.DefaultOutputDirectory);
        Assert.Equal(120, reloaded.WindowX);
        Assert.Equal(80, reloaded.WindowY);
    }

    // ---------------------------------------------------------------------
    // Two-pass encoding was entirely broken: pass 1 wrote to NUL / /dev/null
    // without -f null, so FFmpeg could not infer an output format and died
    // with "Unable to choose an output format". This took out the built-in
    // "YouTube Upload" profile, the only one using two-pass.
    // ---------------------------------------------------------------------

    [FfmpegFact]
    public async Task TwoPass_ProducesAPlayableFile()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var output = Media.Output("twopass.mp4");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.SampleVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Mode = VideoEncodingMode.TwoPassBitrate,
            Bitrate = 500,
            Preset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-aac",
            Container = ContainerFormat.Mp4,
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        Assert.True(File.Exists(output));
        Assert.Equal("h264", Ffmpeg.VideoCodec(output));
        Assert.Equal("aac", Ffmpeg.AudioCodec(output));
        Assert.True(Ffmpeg.Duration(output) > 2.0, "two-pass output should contain the whole clip");
    }

    [Fact]
    public async Task TwoPass_FirstPassNamesTheNullMuxer()
    {
        // Guards the specific flag. FFmpeg cannot infer a muxer from a path with
        // no extension, so pass 1 must say "-f null" explicitly.
        var job = new VideoJob
        {
            Name = "t",
            InputFile = "in.mkv",
            OutputFile = "out.mkv",
            EncoderId = "ffmpeg-x264",
            Settings = new X264Settings { Mode = VideoEncodingMode.TwoPassBitrate, Bitrate = 1000 },
        };

        var recording = new RecordingProcessor();
        var twoPass = new TwoPassProcessor(recording);

        Assert.True(twoPass.CanProcess(job));

        // RecordingProcessor doesn't encode anything — it just captures the args it was handed.
        await twoPass.ProcessAsync(job, new Progress<JobProgress>(), CancellationToken.None);

        Assert.Equal(2, recording.Calls.Count);
        Assert.Contains("-pass 1", recording.Calls[0]);
        Assert.Contains("-f null", recording.Calls[0]);
        Assert.Contains("-pass 2", recording.Calls[1]);
        Assert.DoesNotContain("-f null", recording.Calls[1]);
    }

    /// <summary>Stands in for a real encoder so two-pass argument construction can be inspected.</summary>
    private sealed class RecordingProcessor : IJobProcessor
    {
        public List<string> Calls { get; } = [];
        public string Id => "recording";
        public string DisplayName => "Recording";
        public bool CanProcess(Job job) => true;

        public Task ProcessAsync(Job job, IProgress<JobProgress> progress, CancellationToken ct)
        {
            Calls.Add(((VideoJob)job).Settings.CustomArgs);
            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------------
    // Muxing H.264 into AVI failed ("bitstream malformed, no startcode found")
    // because the intermediate file is Matroska, which stores H.264 length-
    // prefixed, while AVI needs Annex-B. Worse, HEVC into AVI "succeeded" but
    // produced an unplayable rawvideo stream.
    // ---------------------------------------------------------------------

    [FfmpegFact]
    public async Task Avi_MuxesH264ViaAnnexBFilter()
    {
        using var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.SampleVideo, CancellationToken.None);
        var output = Media.Output("annexb.avi");

        var plan = EncodeJobBuilder.Build(new EncodeRequest
        {
            InputFile = Media.SampleVideo,
            OutputFile = output,
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
            Preset = VideoPreset.Ultrafast,
            Quality = 34,
            AudioEncoderId = "ffmpeg-audio-mp3",
            Container = ContainerFormat.Avi,
        }, info);

        var jobs = plan.AllJobs.ToList();
        Assert.True(await RunAsync(services, jobs), Failure(jobs));

        // The stream must survive as real H.264, not fall back to rawvideo.
        Assert.Equal("h264", Ffmpeg.VideoCodec(output));
    }

    [Fact]
    public void Avi_RequestsAnnexBForH264AndHevcOnly()
    {
        Assert.Equal("h264_mp4toannexb", ContainerCompatibility.VideoBitstreamFilter(ContainerFormat.Avi, "h264"));
        Assert.Equal("hevc_mp4toannexb", ContainerCompatibility.VideoBitstreamFilter(ContainerFormat.Ts, "hevc"));
        Assert.Null(ContainerCompatibility.VideoBitstreamFilter(ContainerFormat.Mkv, "h264"));
        Assert.Null(ContainerCompatibility.VideoBitstreamFilter(ContainerFormat.Avi, "vp9"));
    }

    [Fact]
    public void IncompatibleCombinationsAreRejectedBeforeEncoding()
    {
        // HEVC in AVI silently produces a rawvideo stream, so it must be blocked.
        Assert.NotNull(ContainerCompatibility.Validate(ContainerFormat.Avi, "hevc", "mp3"));

        // WebM only carries VP9/AV1 with Opus/Vorbis.
        Assert.NotNull(ContainerCompatibility.Validate(ContainerFormat.Webm, "h264", "opus"));
        Assert.NotNull(ContainerCompatibility.Validate(ContainerFormat.Webm, "vp9", "aac"));
        Assert.Null(ContainerCompatibility.Validate(ContainerFormat.Webm, "vp9", "opus"));

        // Matroska carries everything.
        Assert.Null(ContainerCompatibility.Validate(ContainerFormat.Mkv, "hevc", "flac"));

        // Passthrough audio has an unknowable codec, so it must not be blocked.
        Assert.Null(ContainerCompatibility.Validate(ContainerFormat.Webm, "vp9", null));
    }

    [Fact]
    public void RejectionMessageSuggestsAWorkingContainer()
    {
        var message = ContainerCompatibility.Validate(ContainerFormat.Webm, "h264", "aac");

        Assert.NotNull(message);
        Assert.Contains("Webm", message);
        Assert.Contains("Mkv", message);
    }
}
