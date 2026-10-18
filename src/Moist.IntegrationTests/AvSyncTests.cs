using System.Diagnostics;
using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Broadcast captures and stream-copied cuts start their video and audio at different
/// timestamps. The pipeline encodes them separately, so without alignment the mux would play
/// the picture ~1 s ahead of the sound. Fixture: audio from 0 s, video from 1.0 s, 10 s total.
/// </summary>
[Collection("media")]
public class AvSyncTests(MediaFixture media) : PipelineTestBase(media)
{
    [FfmpegFact]
    public async Task StaggeredStreamStarts_StayInSyncThroughThePipeline()
    {
        var src = Media.Output("staggered_src.mkv");
        Run("ffmpeg",
            "-y -hide_banner -loglevel error -itsoffset 1.0 -f lavfi -i testsrc2=size=320x240:rate=30:duration=9 " +
            $"-f lavfi -i sine=frequency=440:duration=10 -map 0:v -map 1:a -c:v libx264 -preset ultrafast -c:a aac \"{src}\"");

        var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(src);
        var stagger = (info.Video!.StartTime - info.AudioStreams[0].StartTime).TotalSeconds;
        Assert.InRange(stagger, 0.9, 1.1);                                   // the fixture really is staggered

        var request = new EncodeRequest
        {
            InputFile = src,
            OutputFile = Media.Output("staggered_out.mkv"),
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
        };
        var plan = EncodeJobBuilder.Build(request, info);
        Assert.True(await RunAsync(services, plan.AllJobs.ToList()), Failure(plan.AllJobs));

        // Aligned: the 1 s audio lead was trimmed, so the output runs as long as the VIDEO (9 s),
        // not the audio (10 s) — both now start at the same instant.
        Assert.InRange(Ffmpeg.Duration(request.OutputFile), 8.7, 9.3);
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{exe} failed: {err}");
    }
}
