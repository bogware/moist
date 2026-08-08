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
            .Single(p => p.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase));

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
}
