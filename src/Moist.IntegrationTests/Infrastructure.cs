using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Moist.Core.Jobs;
using Moist.Core.Settings;
using Moist.Services;

// These tests launch real FFmpeg processes. Running them concurrently would spawn a
// storm of encoders and make timings (and the cancellation test) unreliable.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Moist.IntegrationTests;

/// <summary>Detects whether the external tools these tests depend on are present.</summary>
public static class Ffmpeg
{
    private static readonly Lazy<bool> _available = new(() => Probe("ffmpeg") && Probe("ffprobe"));

    public static bool Available => _available.Value;

    public static string? SkipReason => Available
        ? null
        : "FFmpeg/FFprobe are not on PATH — skipping integration tests that need them.";

    private static bool Probe(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            p.WaitForExit(15_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Run ffprobe and return trimmed stdout.</summary>
    public static string Probe(string args, string file)
    {
        using var p = Process.Start(new ProcessStartInfo("ffprobe", $"{args} \"{file}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout.Trim();
    }

    /// <summary>First video stream codec, or empty when there is no video stream.</summary>
    public static string VideoCodec(string file)
        => Probe("-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0", file).Trim(',', ' ', '\r', '\n');

    public static string AudioCodec(string file)
        => Probe("-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0", file).Trim(',', ' ', '\r', '\n');

    public static (int Width, int Height) Resolution(string file)
    {
        var raw = Probe("-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0", file);
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? (w, h)
            : (0, 0);
    }

    public static string StreamTypes(string file)
        => Probe("-v error -show_entries stream=codec_type -of csv=p=0", file).Replace("\r", "").Replace("\n", " ");

    public static string PixelFormat(string file)
        => Probe("-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0", file).Trim(',', ' ', '\r', '\n');

    public static double Duration(string file)
        => double.TryParse(Probe("-v error -show_entries format=duration -of csv=p=0", file),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>A [Fact] that skips itself when FFmpeg isn't installed, so CI stays green.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (Ffmpeg.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>A [Theory] that skips itself when FFmpeg isn't installed.</summary>
public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (Ffmpeg.SkipReason is { } reason) Skip = reason;
    }
}

/// <summary>
/// Generates the sample media the tests encode, once per run, in an isolated temp
/// directory that is removed afterwards.
/// </summary>
public sealed class MediaFixture : IDisposable
{
    public string Root { get; }
    public string SampleVideo { get; }
    public string SubtitleFile { get; }
    public string OutputDir { get; }
    public string TempDir { get; }

    /// <summary>10-bit HDR10 source. Encoding this used to fail outright.</summary>
    public string TenBitVideo { get; }

    /// <summary>Three languaged audio tracks, subtitles, chapters and an attachment.</summary>
    public string RichVideo { get; }

    /// <summary>
    /// Organic-looking content for quality work. testsrc2 is synthetic high-frequency
    /// noise that VMAF scores into a narrow band whatever the CRF, which makes it
    /// useless for proving quality responds to settings. This has no audio.
    /// </summary>
    public string NaturalVideo { get; }

    /// <summary>Three visually distinct segments spliced together, so scene detection has real cuts to find.</summary>
    public string SceneVideo { get; }

    public MediaFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "moist-itest-" + Guid.NewGuid().ToString("N")[..8]);
        OutputDir = Path.Combine(Root, "out");
        TempDir = Path.Combine(Root, "tmp");
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(TempDir);

        SampleVideo = Path.Combine(Root, "sample.mp4");
        SubtitleFile = Path.Combine(Root, "sample.srt");
        TenBitVideo = Path.Combine(Root, "tenbit.mkv");
        RichVideo = Path.Combine(Root, "rich.mkv");
        NaturalVideo = Path.Combine(Root, "natural.mp4");
        SceneVideo = Path.Combine(Root, "scenes.mp4");

        if (!Ffmpeg.Available) return;

        // Short, tiny and deterministic — every test encodes this.
        Run("ffmpeg", $"-y -v error -f lavfi -i testsrc2=size=320x240:rate=25:duration=3 " +
                      $"-f lavfi -i sine=frequency=440:duration=3 " +
                      $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \"{SampleVideo}\"");

        File.WriteAllText(SubtitleFile,
            "1\n00:00:00,200 --> 00:00:01,500\nfirst line\n\n2\n00:00:01,800 --> 00:00:02,800\nsecond line\n");

        BuildTenBitSource();
        BuildRichSource();

        // Mandelbrot gives smooth gradients and organic detail, so quality responds to
        // CRF the way real footage does.
        Run("ffmpeg", $"-y -v error -f lavfi -i mandelbrot=size=480x270:rate=25 -t 8 " +
                      $"-c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p \"{NaturalVideo}\"");

        // Three clearly different segments so scene detection has genuine cuts to find,
        // plus audio so the chunked path exercises muxing as well.
        Run("ffmpeg",
            $"-y -v error " +
            $"-f lavfi -i testsrc2=size=320x180:rate=25:duration=3 " +
            $"-f lavfi -i mandelbrot=size=320x180:rate=25 " +
            $"-f lavfi -i smptebars=size=320x180:rate=25:duration=3 " +
            $"-f lavfi -i sine=frequency=440:duration=9 " +
            $"-filter_complex \"[1:v]trim=duration=3,setpts=PTS-STARTPTS[m];[0:v][m][2:v]concat=n=3:v=1[v]\" " +
            $"-map \"[v]\" -map 3:a -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest " +
            $"\"{SceneVideo}\"");
    }

    /// <summary>10-bit HDR10: BT.2020 primaries, PQ transfer, mastering display and CLL.</summary>
    private void BuildTenBitSource()
    {
        Run("ffmpeg",
            $"-y -v error -f lavfi -i testsrc2=size=320x240:rate=25:duration=2 " +
            $"-f lavfi -i sine=frequency=440:duration=2 " +
            $"-c:v libx265 -pix_fmt yuv420p10le -preset ultrafast " +
            $"-x265-params \"colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:" +
            $"master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400\" " +
            $"-color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc " +
            $"-c:a aac -shortest \"{TenBitVideo}\"");
    }

    /// <summary>Three languaged audio tracks, a subtitle, chapters and a font attachment.</summary>
    private void BuildRichSource()
    {
        var chapters = Path.Combine(Root, "chapters.txt");
        var attachment = Path.Combine(Root, "TestFont.ttf");
        var subtitle = Path.Combine(Root, "rich.srt");

        // ffmetadata must not carry a BOM or FFmpeg rejects it.
        File.WriteAllText(chapters,
            ";FFMETADATA1\ntitle=Rich Test Source\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=Opening\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=4000\ntitle=Middle\n" +
            "[CHAPTER]\nTIMEBASE=1/1000\nSTART=4000\nEND=6000\ntitle=Ending\n",
            new System.Text.UTF8Encoding(false));

        File.WriteAllText(attachment, "TEST FONT ATTACHMENT PAYLOAD");
        File.WriteAllText(subtitle, "1\n00:00:00,500 --> 00:00:02,000\nline one\n\n2\n00:00:03,000 --> 00:00:05,000\nline two\n");

        Run("ffmpeg",
            $"-y -v error " +
            $"-f lavfi -i testsrc2=size=320x240:rate=25:duration=6 " +
            $"-f lavfi -i sine=frequency=440:duration=6 " +
            $"-f lavfi -i sine=frequency=880:duration=6 " +
            $"-f lavfi -i sine=frequency=220:duration=6 " +
            $"-i \"{subtitle}\" -f ffmetadata -i \"{chapters}\" " +
            $"-map 0:v -map 1:a -map 2:a -map 3:a -map 4:s -map_metadata 5 " +
            $"-metadata:s:a:0 language=eng -metadata:s:a:0 title=\"English Stereo\" " +
            $"-metadata:s:a:1 language=jpn -metadata:s:a:1 title=\"Japanese Stereo\" " +
            $"-metadata:s:a:2 language=fra -metadata:s:a:2 title=Commentary " +
            $"-metadata:s:s:0 language=eng " +
            $"-attach \"{attachment}\" -metadata:s:t:0 mimetype=application/x-truetype-font " +
            $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -c:s srt " +
            $"\"{RichVideo}\"");
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"fixture setup failed: {exe} {args}\n{err}");
    }

    public string Output(string name) => Path.Combine(OutputDir, name);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

[CollectionDefinition("media")]
public sealed class MediaCollection : ICollectionFixture<MediaFixture>;

/// <summary>Shared helpers for driving the real job pipeline in tests.</summary>
public abstract class PipelineTestBase(MediaFixture media)
{
    protected MediaFixture Media { get; } = media;

    protected AppServices CreateServices(int concurrency = 4)
    {
        var services = new AppServices(LogLevel.Warning, logToStandardError: true);
        services.Settings.FFmpegPath = "ffmpeg";
        services.Settings.FFprobePath = "ffprobe";
        services.Settings.TempDirectory = Media.TempDir;
        services.JobQueue.MaxConcurrentJobs = concurrency;
        return services;
    }

    /// <summary>Enqueue a plan, run it to completion and assert every job succeeded.</summary>
    protected static async Task<bool> RunAsync(AppServices services, IReadOnlyList<Job> jobs, TimeSpan? timeout = null)
    {
        foreach (var job in jobs) services.JobQueue.Enqueue(job);

        var run = services.JobQueue.StartAsync();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));

        while (DateTime.UtcNow < deadline)
        {
            if (jobs.All(j => j.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
                break;
            await Task.Delay(100);
        }

        services.JobQueue.Stop();
        try { await run.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }

        return jobs.All(j => j.Status == JobStatus.Completed);
    }

    protected static string Failure(IEnumerable<Job> jobs)
    {
        var failed = jobs.FirstOrDefault(j => j.Status == JobStatus.Failed);
        if (failed is null) return "no job reported a failure";
        var tail = (failed.ErrorMessage ?? "").Split('\n').TakeLast(4);
        return $"{failed.Name}: {string.Join(" | ", tail)}";
    }
}

/// <summary>
/// Backs up the real settings file and restores it, so tests that exercise
/// persistence can't damage the developer's actual configuration.
/// </summary>
public sealed class SettingsSandbox : IDisposable
{
    private readonly string _path;
    private readonly string? _backup;

    public SettingsSandbox()
    {
        _path = MoistSettings.GetSettingsPath();
        _backup = File.Exists(_path) ? File.ReadAllText(_path) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_backup is not null) File.WriteAllText(_path, _backup);
            else if (File.Exists(_path)) File.Delete(_path);
        }
        catch { /* best effort */ }
    }
}
