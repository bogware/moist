using Moist.Cli;
using Moist.Core.Encoders.Video;
using Moist.Core.Muxers;

namespace Moist.Core.Tests;

/// <summary>
/// Pure parsing tests for the CLI — no FFmpeg, no filesystem work.
/// </summary>
public class CliParsingTests
{
    [Fact]
    public void SeparateValue_IsAttachedToItsOption()
    {
        var cl = CommandLine.Parse(["input.mkv", "--crf", "20"]);

        Assert.Equal(20, cl.Int("crf"));
        Assert.Equal(["input.mkv"], cl.Positional);
    }

    [Fact]
    public void EqualsForm_IsSupported()
    {
        var cl = CommandLine.Parse(["--crf=18.5", "--preset=slow"]);

        Assert.Equal(18.5, cl.Double("crf"));
        Assert.Equal(VideoPreset.Slow, cl.Enum<VideoPreset>("preset"));
    }

    [Fact]
    public void BareFlag_DoesNotSwallowTheNextArgument()
    {
        // Regression guard: "--audio-only input.mkv" must not read the filename as the flag's value.
        var cl = CommandLine.Parse(["--audio-only", "input.mkv"]);

        Assert.True(cl.Flag("audio-only"));
        Assert.Equal(["input.mkv"], cl.Positional);
    }

    [Fact]
    public void ShortOptions_Work()
    {
        var cl = CommandLine.Parse(["in.mkv", "-o", "out.mkv", "-j", "4"]);

        Assert.Equal("out.mkv", cl.String("output", "o"));
        Assert.Equal(4, cl.Int("jobs", "j"));
        Assert.Equal(["in.mkv"], cl.Positional);
    }

    [Fact]
    public void RepeatedOption_CollectsEveryValue()
    {
        var cl = CommandLine.Parse(["--filter", "resize:width=8", "--filter", "unsharp:luma_amount=1"]);

        Assert.Equal(2, cl.Many("filter").Count);
    }

    [Fact]
    public void DoubleDash_TreatsTheRestAsPositional()
    {
        var cl = CommandLine.Parse(["--crf", "20", "--", "--not-an-option.mkv"]);

        Assert.Equal(20, cl.Int("crf"));
        Assert.Equal(["--not-an-option.mkv"], cl.Positional);
    }

    [Fact]
    public void UnknownOptions_AreReportedAfterTheKnownOnesAreRead()
    {
        var cl = CommandLine.Parse(["--crf", "20", "--typo", "3"]);
        cl.Int("crf");

        Assert.Equal(["typo"], cl.UnknownOptions());
    }

    [Fact]
    public void BadNumber_IsAUsageError()
    {
        var cl = CommandLine.Parse(["--crf", "veryhigh"]);

        var ex = Assert.Throws<UsageException>(() => cl.Int("crf"));
        Assert.Contains("crf", ex.Message);
    }

    [Fact]
    public void BadEnum_ListsTheValidValues()
    {
        var cl = CommandLine.Parse(["--container", "flv"]);

        var ex = Assert.Throws<UsageException>(() => cl.Enum<ContainerFormat>("container"));
        Assert.Contains("mkv", ex.Message);
        Assert.Contains("mp4", ex.Message);
    }

    [Theory]
    [InlineData("crf", VideoEncodingMode.ConstantRateFactor)]
    [InlineData("quality", VideoEncodingMode.ConstantQuality)]
    [InlineData("bitrate", VideoEncodingMode.SinglePassBitrate)]
    [InlineData("twopass", VideoEncodingMode.TwoPassBitrate)]
    [InlineData("2pass", VideoEncodingMode.TwoPassBitrate)]
    public void EncodingModes_MapFromFriendlyNames(string raw, VideoEncodingMode expected)
        => Assert.Equal(expected, EncodeOptions.ParseMode(raw));

    [Fact]
    public void UnknownEncodingMode_IsAUsageError()
        => Assert.Throws<UsageException>(() => EncodeOptions.ParseMode("lossless"));

    [Fact]
    public void EnumParsing_IgnoresCaseAndDashes()
    {
        var cl = CommandLine.Parse(["--container", "WEBM", "--preset", "very-slow"]);

        Assert.Equal(ContainerFormat.Webm, cl.Enum<ContainerFormat>("container"));
        Assert.Equal(VideoPreset.Veryslow, cl.Enum<VideoPreset>("preset"));
    }

    [Fact]
    public void MissingOption_ReturnsNullSoDefaultsApply()
    {
        var cl = CommandLine.Parse(["input.mkv"]);

        Assert.Null(cl.Int("crf"));
        Assert.Null(cl.String("output", "o"));
        Assert.False(cl.Flag("audio-only"));
    }
}
