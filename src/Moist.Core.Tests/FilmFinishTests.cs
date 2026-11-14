using System.Text.Json;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Profiles;
using Moist.Filters;
using Moist.Filters.Engines;
using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>
/// The film-first finish: a film-grain filter, a configurable post-upscale finish for the
/// Real-ESRGAN path (grain must come after the model, which would scrub it), the encoder tune
/// reaching that path, and the two "Film Restore" profiles that use them.
/// </summary>
public class FilmFinishTests
{
    private static FilterDefinition Grain() => new FFmpegFilterEngine().GetAvailableFilters().Single(f => f.Id == "grain");

    [Fact]
    public void Grain_IsTemporalAveragedLumaNoise_ByDefault()
    {
        var af = new AppliedFilter { Filter = Grain(), ParameterValues = new() { ["strength"] = 5 } };
        Assert.Equal("noise=c0s=5:c0f=t+a", FFmpegFilterEngine.BuildGrainFilter(af));
    }

    [Fact]
    public void Grain_ChromaOption_AddsHalfStrengthColourGrain()
    {
        var af = new AppliedFilter { Filter = Grain(), ParameterValues = new() { ["strength"] = 8, ["chroma"] = true } };
        Assert.Equal("noise=c0s=8:c0f=t+a:c1s=4:c1f=t+a:c2s=4:c2f=t+a", FFmpegFilterEngine.BuildGrainFilter(af));
    }

    [Fact]
    public void Grain_ZeroStrength_IsANoOp()
        => Assert.Equal("null", FFmpegFilterEngine.BuildGrainFilter(new AppliedFilter { Filter = Grain(), ParameterValues = new() { ["strength"] = 0 } }));

    private static VideoJob AiJob(string? finish, VideoTune tune = VideoTune.None) => new()
    {
        Name = "ai", InputFile = "in.mkv", OutputFile = "out.mkv", EncoderId = "ffmpeg-x265",
        Settings = new X265Settings { Quality = 16, Tune = tune }, AiUpscaleModel = "x4plus", AiFinishFilterScript = finish,
    };

    private static string Vf(VideoJob vj)
    {
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(vj, "%06d.jpg", "24/1", "out.mkv");
        return args[Array.IndexOf(args, "-vf") + 1];
    }

    [Fact]
    public void AiFinish_DefaultsToTheLightCasAndDeband()
        => Assert.Equal("scale=-2:1080:flags=spline,cas=strength=0.4,deband=range=16:blur=1", Vf(AiJob(null)));

    [Fact]
    public void AiFinish_ProfileFinishRunsAfterTheScale()
        => Assert.Equal("scale=-2:1080:flags=spline,cas=strength=0.4,noise=c0s=5:c0f=t+a", Vf(AiJob("cas=strength=0.4,noise=c0s=5:c0f=t+a,")));

    [Fact]
    public void AiEncode_PassesTheTune_SoTuneGrainKeepsTheGrain()
    {
        var args = RealEsrganVideoProcessor.BuildEncodeArgs(AiJob(null, VideoTune.Grain), "%06d.jpg", "24/1", "out.mkv");
        Assert.Equal("grain", args[Array.IndexOf(args, "-tune") + 1]);
        Assert.DoesNotContain("-tune", RealEsrganVideoProcessor.BuildEncodeArgs(AiJob(null), "%06d.jpg", "24/1", "out.mkv"));
    }

    [Fact]
    public void WorkKey_ChangesWithTheFinish_SoAResumeNeverMixesFinishes()
    {
        Assert.NotEqual(RealEsrganVideoProcessor.WorkKey(AiJob(null)), RealEsrganVideoProcessor.WorkKey(AiJob("noise=c0s=5:c0f=t+a")));
        Assert.NotEqual(RealEsrganVideoProcessor.WorkKey(AiJob(null)), RealEsrganVideoProcessor.WorkKey(AiJob(null, VideoTune.Grain)));
    }

    [Fact]
    public void ProfileFinish_RendersAfterAJsonRoundTrip()
    {
        // On disk the parameters come back as JsonElements; the finish must still render.
        var profile = ProfileManager.GetBuiltInProfiles().Single(p => p.Name == "Film Restore to 1080p (Real-ESRGAN)");
        var json = JsonSerializer.Serialize(profile);
        var loaded = JsonSerializer.Deserialize<EncodingProfile>(json)!;
        var services = new FilterRegistry();
        services.Register(new FFmpegFilterEngine());
        Assert.Equal("cas=strength=0.4,eq=brightness=0:contrast=1.06:saturation=1.05:gamma=1,noise=c0s=5:c0f=t+a",
            ProfileFinish.Render(loaded, services));
        Assert.Null(ProfileFinish.Render(new EncodingProfile(), services));
    }

    [Theory]
    [InlineData("Film Restore to 1080p (Real-ESRGAN)", true)]
    [InlineData("Film Restore to 1080p (Classic)", false)]
    public void FilmRestoreProfiles_AreFilmFirst(string name, bool ai)
    {
        var p = ProfileManager.GetBuiltInProfiles().Single(x => x.Name == name);

        // No denoiser: it erases grain and fine detail with the noise. Only a light de-block.
        Assert.DoesNotContain(p.Filters, f => f.FilterId is "hqdn3d" or "nlmeans");
        Assert.DoesNotContain(p.AiFinishFilters, f => f.FilterId is "hqdn3d" or "nlmeans");
        Assert.Equal("deblock", p.Filters[0].FilterId);

        Assert.Equal(VideoTune.Grain, p.VideoTune);
        Assert.Equal(VideoBitDepth.Ten, p.BitDepth);

        // Grain is the last thing laid down — after the model on the AI path, which would scrub it.
        var finish = ai ? p.AiFinishFilters : p.Filters;
        Assert.Equal("grain", finish[^1].FilterId);
        Assert.Equal(ai ? "x4plus" : null, p.AiUpscaleModel);
        if (ai) Assert.DoesNotContain(p.Filters, f => f.FilterId == "grain");
    }
}
