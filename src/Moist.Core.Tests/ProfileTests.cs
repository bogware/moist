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
}
