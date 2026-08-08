using Moist.Core.Muxers;

namespace Moist.Core.Tests;

public class ContainerFormatTests
{
    [Theory]
    [InlineData("out.mp4", ContainerFormat.Mp4)]
    [InlineData("C:/a b/My Movie.MKV", ContainerFormat.Mkv)]   // case-insensitive, spaces, full path
    [InlineData("clip.avi", ContainerFormat.Avi)]
    [InlineData("stream.ts", ContainerFormat.Ts)]
    [InlineData("v.m4v", ContainerFormat.Mp4)]                 // alias
    [InlineData("mov", ContainerFormat.Mov)]                   // bare extension
    [InlineData(".webm", ContainerFormat.Webm)]                // dotted bare extension
    public void FromExtension_RecognizesKnownContainers(string input, ContainerFormat expected)
    {
        Assert.Equal(expected, ContainerFormatExtensions.FromExtension(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noextension")]
    [InlineData("file.xyz")]
    [InlineData(null)]
    public void FromExtension_ReturnsNull_ForUnknownOrEmpty(string? input)
    {
        // Null lets the caller keep its own default rather than being forced to Matroska.
        Assert.Null(ContainerFormatExtensions.FromExtension(input));
    }

    [Fact]
    public void FromExtension_RoundTripsGetExtension()
    {
        foreach (ContainerFormat c in Enum.GetValues(typeof(ContainerFormat)))
            Assert.Equal(c, ContainerFormatExtensions.FromExtension(c.GetExtension()));
    }
}
