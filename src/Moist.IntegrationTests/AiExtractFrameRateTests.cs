using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.IntegrationTests;

/// <summary>
/// The frame-based AI processors (Real-ESRGAN, RIFE) extract frames through the pre-clean filter
/// chain and encode the results at a frame rate <i>measured</i> through that chain. This pins the
/// measurement against a real ffmpeg: a cadence-changing filter must yield the exact post-filter
/// rate (24000/1001, not the 60000/1001 source), an unfiltered chain must keep the source rate, and
/// trims must be honoured so a snippet preview extracts a snippet, not the film. Needs only ffmpeg.
/// </summary>
public class AiExtractFrameRateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moist_aiextract_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;

    public AiExtractFrameRateTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "src5994.mp4");
        if (Ffmpeg.Available)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=60000/1001:d=4",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", _source,
            }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("fixture encode failed: " + err);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static VideoJob Job(string input, string filter) => new()
    {
        Name = "ai-extract", InputFile = input, OutputFile = "unused.mkv", EncoderId = "ffmpeg-x265",
        Settings = new VideoEncoderSettings(), AiUpscaleModel = "x4plus", FilterScript = filter,
    };

    /// <summary>The ivtc chain exactly as the FFmpeg filter engine emits it for a bobbed 59.94 source.</summary>
    private const string Ivtc = "fps=source_fps,tinterlace=mode=interleave_top,fieldmatch=order=tff:combmatch=full,decimate";

    [FfmpegFact]
    public async Task IvtcPreClean_MeasuresTheExactFilmRate()
    {
        var rate = await RealEsrganVideoProcessor.MeasureOutputRateAsync("ffmpeg", Job(_source, Ivtc), "60000/1001", NullLogger.Instance, CancellationToken.None);
        Assert.Equal("24000/1001", rate);
    }

    [FfmpegFact]
    public async Task FpsPreClean_MeasuresTheRequestedRate()
    {
        // fps=24 on an NTSC source is 24, not 24000/1001 — 0.1% apart, seconds of drift over a film.
        Assert.Equal("24/1", await RealEsrganVideoProcessor.MeasureOutputRateAsync("ffmpeg", Job(_source, "fps=24"), "60000/1001", NullLogger.Instance, CancellationToken.None));
        Assert.Equal("24000/1001", await RealEsrganVideoProcessor.MeasureOutputRateAsync("ffmpeg", Job(_source, "fps=24000/1001"), "60000/1001", NullLogger.Instance, CancellationToken.None));
    }

    [FfmpegFact]
    public async Task NoPreClean_KeepsTheSourceRate()
    {
        Assert.Equal("60000/1001", await RealEsrganVideoProcessor.MeasureOutputRateAsync("ffmpeg", Job(_source, ""), "60000/1001", NullLogger.Instance, CancellationToken.None));
    }

    [FfmpegFact]
    public async Task BrokenChain_FallsBackToTheSourceRateInsteadOfThrowing()
    {
        var rate = await RealEsrganVideoProcessor.MeasureOutputRateAsync("ffmpeg", Job(_source, "nosuchfilter=1"), "60000/1001", NullLogger.Instance, CancellationToken.None);
        Assert.Equal("60000/1001", rate);
    }

    [FfmpegFact]
    public async Task Trims_ExtractOnlyTheSnippet()
    {
        var job = Job(_source, Ivtc);
        job.TrimStart = TimeSpan.FromSeconds(1);
        job.TrimFrames = 24;
        var framesDir = Path.Combine(_root, "snippet");
        Directory.CreateDirectory(framesDir);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in RealEsrganVideoProcessor.BuildExtractArgs(job, Path.Combine(framesDir, "%06d.png")))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, $"extract failed ({p.ExitCode}):\n{await stderr}");

        Assert.Equal(24, Directory.GetFiles(framesDir, "*.png").Length);
        Assert.Contains("frame=", await stdout);                        // the -progress heartbeat the watchdog relies on
    }

    /// <summary>A broadcast capture with a reception dropout has a pts gap and no frames. decimate
    /// would collapse it and the video would run ahead of the audio; the ivtc chain's leading
    /// fps=source_fps duplicates frames across the gap so the frame count — and the timeline — hold.</summary>
    [FfmpegFact]
    public async Task IvtcFillsTimestampGaps_SoTheTimelineSurvivesADropout()
    {
        // Drop frames 50-58 of the 60p fixture (a 150 ms hole) ahead of the ivtc chain.
        var holed = "select='not(between(n,50,58))'," + Ivtc;
        var (pngs, _) = await ExtractAsync(Job(_source, holed), Path.Combine(_root, "holed"));
        var (full, _) = await ExtractAsync(Job(_source, Ivtc), Path.Combine(_root, "full"));
        Assert.InRange(full, 95, 97);
        Assert.Equal(full, pngs);                                        // the hole was filled, not collapsed
    }

    private static async Task<(int Pngs, string Stderr)> ExtractAsync(VideoJob job, string framesDir)
    {
        Directory.CreateDirectory(framesDir);
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in RealEsrganVideoProcessor.BuildExtractArgs(job, Path.Combine(framesDir, "%06d.png")))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        await stdout;
        var err = await stderr;
        Assert.True(p.ExitCode == 0, $"extract failed ({p.ExitCode}):\n{err}");
        return (Directory.GetFiles(framesDir, "*.png").Length, err);
    }
}
