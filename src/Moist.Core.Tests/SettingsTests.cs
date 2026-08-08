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

    [Fact]
    public void OutputLocationDefaults_AreSensible()
    {
        var settings = new MoistSettings();

        // Preserve the historical auto-fill behaviour: default folder, "_encoded" suffix, no prompt.
        Assert.False(settings.PromptForOutputLocation);
        Assert.Equal("_encoded", settings.DefaultOutputSuffix);
        Assert.False(settings.OpenOutputFolderWhenDone);
    }

    [Fact]
    public void Repaired_StripsInvalidCharsFromOutputSuffix()
    {
        // A suffix carrying a path separator would build a broken output path.
        var settings = new MoistSettings { DefaultOutputSuffix = "_re/stored" }.Repaired();
        Assert.DoesNotContain('/', settings.DefaultOutputSuffix);
        Assert.DoesNotContain('\\', settings.DefaultOutputSuffix);
    }
}
