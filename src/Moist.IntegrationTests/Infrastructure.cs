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

    public MediaFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "moist-itest-" + Guid.NewGuid().ToString("N")[..8]);
        OutputDir = Path.Combine(Root, "out");
        TempDir = Path.Combine(Root, "tmp");
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(TempDir);

        SampleVideo = Path.Combine(Root, "sample.mp4");
        SubtitleFile = Path.Combine(Root, "sample.srt");

        if (!Ffmpeg.Available) return;

        // Short, tiny and deterministic — every test encodes this.
        Run("ffmpeg", $"-y -v error -f lavfi -i testsrc2=size=320x240:rate=25:duration=3 " +
                      $"-f lavfi -i sine=frequency=440:duration=3 " +
                      $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest \"{SampleVideo}\"");

        File.WriteAllText(SubtitleFile,
            "1\n00:00:00,200 --> 00:00:01,500\nfirst line\n\n2\n00:00:01,800 --> 00:00:02,800\nsecond line\n");
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
