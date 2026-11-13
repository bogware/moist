using Moist.Core.Chunking;

namespace Moist.Core.Tests;

/// <summary>
/// ffprobe's frame count, as every chunk plan, join verification, resume check and deep health scan
/// reads it. An MPEG-TS lists each stream again under its program, so a broadcast capture prints the
/// count twice with trailing commas; the old whole-string parse returned -1 and chunk planning fell
/// back to a duration estimate that included the audio's head start (4509 planned, 4462 real).
/// </summary>
public class FrameCountParsingTests
{
    [Theory]
    [InlineData("4462\n", 4462)]
    [InlineData("4462,\n", 4462)]
    [InlineData("4462,\n\n4462,\n", 4462)]          // MPEG-TS: stream + program entry
    [InlineData("\r\n  71,\r\n", 71)]
    [InlineData("", -1)]
    [InlineData("N/A\n", -1)]
    public void ParseFrameCount_TakesTheFirstNumber(string output, long expected)
        => Assert.Equal(expected, FFmpegConcatProcessor.ParseFrameCount(output));
}
