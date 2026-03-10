using Moist.Core.Settings;

namespace Moist.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var settings = new MoistSettings();

        Assert.Equal("ffmpeg", settings.FFmpegPath);
        Assert.Equal("ffprobe", settings.FFprobePath);
        Assert.Equal(1, settings.MaxConcurrentJobs);
        Assert.True(settings.DeleteTempFiles);
        Assert.Equal("moist Dark", settings.Theme);
        Assert.Equal("ffmpeg-x265", settings.DefaultVideoEncoder);
        Assert.Equal("ffmpeg-audio-opus", settings.DefaultAudioEncoder);
    }

    [Fact]
    public void SettingsPath_IsNotEmpty()
    {
        var path = MoistSettings.GetSettingsPath();
        Assert.False(string.IsNullOrEmpty(path));
        Assert.EndsWith("settings.json", path);
    }
}
