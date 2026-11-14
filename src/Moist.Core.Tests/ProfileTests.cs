using Moist.Core.Encoders.Video;
using Moist.Core.Muxers;
using Moist.Core.Profiles;

namespace Moist.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void EncodingProfile_DefaultValues()
    {
        var profile = new EncodingProfile();

        Assert.Equal("Untitled", profile.Name);
        Assert.Equal("ffmpeg-x265", profile.VideoEncoderId);
        Assert.Equal("ffmpeg-audio-opus", profile.AudioEncoderId);
        Assert.Equal(ContainerFormat.Mkv, profile.Container);
        Assert.Equal(VideoEncodingMode.ConstantRateFactor, profile.VideoMode);
    }

    [Fact]
    public void GetBuiltInProfiles_ReturnsProfiles()
    {
        var profiles = ProfileManager.GetBuiltInProfiles();
        Assert.NotEmpty(profiles);
        Assert.Contains(profiles, p => p.Name.Contains("H.265"));
        Assert.Contains(profiles, p => p.Name.Contains("Lossless"));
    }

    [Fact]
    public void RestoreProfile_CarriesTheFullPipeline()
    {
        var restore = ProfileManager.GetBuiltInProfiles()
            .Single(p => p.Name == "Restore SD to HD (vintage)");

        Assert.Equal("ffmpeg-x265", restore.VideoEncoderId);
        Assert.Equal(VideoBitDepth.Ten, restore.BitDepth);
        Assert.Equal("ffmpeg", restore.FilterEngineId);

        // The restoration order is load-bearing: de-block and denoise before the upscale, sharpen after.
        var ids = restore.Filters.Select(f => f.FilterId).ToList();
        Assert.Equal(new[] { "deblock", "hqdn3d", "resize", "cas", "deband" }, ids);
    }

    [Fact]
    public void RestoreAiProfile_SwapsSuperResForTheSplineScale()
    {
        var ai = ProfileManager.GetBuiltInProfiles()
            .Single(p => p.Name.Contains("(GPU)", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(VideoBitDepth.Ten, ai.BitDepth);
        Assert.Equal("ffmpeg", ai.FilterEngineId);
        // Same pipeline as the classical restore, with the GPU neural upscale in the resize slot.
        Assert.Equal(new[] { "deblock", "hqdn3d", "superres", "cas", "deband" },
            ai.Filters.Select(f => f.FilterId));
    }

    [Theory]
    [InlineData("Broadcast 720p60 Film - B&W to 1080p",    null,     new[] { "crop", "ivtc", "grayscale", "deblock", "hqdn3d", "superres", "cas", "deband" })]
    [InlineData("Broadcast 720p60 Film - Colour to 1080p", null,     new[] { "crop", "ivtc", "deblock", "hqdn3d", "superres", "cas", "deband" })]
    [InlineData("Broadcast 720p60 Film - B&W to 1080p (Premium Real-ESRGAN)",    "x4plus", new[] { "crop", "ivtc", "grayscale", "deblock", "hqdn3d" })]
    [InlineData("Broadcast 720p60 Film - Colour to 1080p (Premium Real-ESRGAN)", "x4plus", new[] { "crop", "ivtc", "deblock", "hqdn3d" })]
    public void BroadcastProfiles_CadenceAndCropComeFirst_ThenCleanBeforeUpscale(string name, string? aiModel, string[] chain)
    {
        var p = ProfileManager.GetBuiltInProfiles().Single(x => x.Name == name);

        Assert.Equal("ffmpeg-x265", p.VideoEncoderId);
        Assert.Equal(VideoBitDepth.Ten, p.BitDepth);
        Assert.Equal(18, p.VideoQuality);
        Assert.Equal("ffmpeg", p.FilterEngineId);
        Assert.Equal(aiModel, p.AiUpscaleModel);
        if (aiModel is not null) Assert.Equal(1080, p.AiUpscaleHeight);
        // ivtc must see the bobbed 60p before anything touches it, and the crop must precede the
        // upscale so the 1080p frame is all picture; grayscale only on the B&W era. The premium
        // variants stop at the pre-clean: the Real-ESRGAN processor adds the upscale + finish.
        Assert.Equal(chain, p.Filters.Select(f => f.FilterId));
        Assert.Equal(88, p.Filters[0].Parameters["left"]);
        Assert.Equal(88, p.Filters[0].Parameters["right"]);
        Assert.Equal(true, p.Filters[1].Parameters["bobbed"]);
    }

    [Fact]
    public void BroadcastProfile_SurvivesTheJsonRoundTrip_AndGeneratesTheExpectedChain()
    {
        // Built-ins are materialized to JSON and read back (params become JsonElement) before the
        // CLI or GUI ever build a chain from them, so assert on the script of the round-tripped copy.
        var built = ProfileManager.GetBuiltInProfiles().Single(x => x.Name == "Broadcast 720p60 Film - B&W to 1080p");
        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        var loaded = System.Text.Json.JsonSerializer.Deserialize<EncodingProfile>(
            System.Text.Json.JsonSerializer.Serialize(built, options), options)!;

        // No GPU probe in unit tests: pin the superres fallback so the tail is deterministic.
        var engine = new Moist.Filters.Engines.FFmpegFilterEngine { IsSuperResAvailable = () => false };
        var available = engine.GetAvailableFilters();
        var chain = new Moist.Filters.FilterChain();
        foreach (var pf in loaded.Filters)
            chain.Add(available.Single(f => f.Id == pf.FilterId), new Dictionary<string, object>(pf.Parameters));
        var script = engine.GenerateScript(chain, "in.ts");

        Assert.StartsWith(
            "crop=iw-176:ih-0:88:0,fps=source_fps,tinterlace=mode=interleave_top,fieldmatch=order=tff:combmatch=full,decimate,hue=s=0,spp=quality=3:qp=4,hqdn3d=2:1.5:3,",
            script);
        // superres falls back to a spline scale where no Vulkan GPU is available.
        Assert.EndsWith("scale=-2:1080:flags=spline,cas=strength=0.4,deband=range=16:blur=1", script);
    }

    [Theory]
    [InlineData("Broadcast 720p60 Film - Faithful 720p (B&W)",  new[] { "crop", "grayscale", "cas", "eq" })]
    [InlineData("Broadcast 720p60 Film - Faithful 720p (Colour)", new[] { "crop", "cas", "eq" })]
    public void FaithfulBroadcastProfiles_KeepNativeRateAndSize_AndPreserveGrain(string name, string[] chain)
    {
        var p = ProfileManager.GetBuiltInProfiles().Single(x => x.Name == name);

        // Nothing that resamples time or space, nothing that denoises: the source look is the point.
        Assert.Equal(chain, p.Filters.Select(f => f.FilterId));
        Assert.DoesNotContain(p.Filters, f => f.FilterId is "ivtc" or "resize" or "superres" or "hqdn3d" or "nlmeans" or "fps");
        Assert.Null(p.AiUpscaleModel);

        Assert.Equal("ffmpeg-x265", p.VideoEncoderId);
        Assert.Equal(VideoTune.Grain, p.VideoTune);                  // x265 must not smooth or strobe the grain
        Assert.Equal(VideoBitDepth.Ten, p.BitDepth);
        Assert.Equal(17, p.VideoQuality);
        Assert.Equal("ffmpeg-audio-copy", p.AudioEncoderId);       // the broadcast AC3, untouched
        Assert.Equal(ContainerFormat.Mkv, p.Container);

        var eq = p.Filters.Single(f => f.FilterId == "eq").Parameters;
        Assert.Equal(1.04, eq["contrast"]);
        Assert.Equal(name.Contains("B&W"), !eq.ContainsKey("saturation"));   // no saturation push on a monochrome picture
    }
}
