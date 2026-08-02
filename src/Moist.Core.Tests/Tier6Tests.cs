using Moist.Cli;
using Moist.Core.Encoders.Video;
using Moist.Core.Media;
using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>
/// Chroma, bit depth, subtitle handling and the warnings that stop something being
/// silently destroyed. No encoding.
/// </summary>
public class Tier6Tests
{
    private static EncodeRequest Request() => new()
    {
        InputFile = "in.mkv",
        OutputFile = "out.mkv",
        TempDirectory = Path.GetTempPath(),
    };

    // ---------------- pixel format resolution ----------------

    [Fact]
    public void NothingRequestedMeansNoPixelFormatAtAll()
    {
        // Silence is how the encoder is told to follow the source exactly.
        var settings = new VideoEncoderSettings();
        Assert.Null(settings.ResolvePixelFormat(10));
    }

    [Theory]
    [InlineData(VideoBitDepth.Eight, ChromaSubsampling.Auto, "yuv420p")]
    [InlineData(VideoBitDepth.Ten, ChromaSubsampling.Auto, "yuv420p10le")]
    [InlineData(VideoBitDepth.Twelve, ChromaSubsampling.Auto, "yuv420p12le")]
    [InlineData(VideoBitDepth.Eight, ChromaSubsampling.Yuv422, "yuv422p")]
    [InlineData(VideoBitDepth.Ten, ChromaSubsampling.Yuv422, "yuv422p10le")]
    [InlineData(VideoBitDepth.Ten, ChromaSubsampling.Yuv444, "yuv444p10le")]
    [InlineData(VideoBitDepth.Twelve, ChromaSubsampling.Yuv444, "yuv444p12le")]
    public void DepthAndChromaCombine(VideoBitDepth depth, ChromaSubsampling chroma, string expected)
    {
        var settings = new VideoEncoderSettings { BitDepth = depth, Chroma = chroma };
        Assert.Equal(expected, settings.ResolvePixelFormat());
    }

    [Fact]
    public void AskingOnlyForChromaKeepsTheSourceDepth()
    {
        // Requesting 4:4:4 on a 10-bit master must not quietly drop it to 8-bit.
        var settings = new VideoEncoderSettings { Chroma = ChromaSubsampling.Yuv444 };
        Assert.Equal("yuv444p10le", settings.ResolvePixelFormat(sourceBitDepth: 10));
    }

    [Fact]
    public void AnExplicitPixelFormatOverridesEverything()
    {
        var settings = new VideoEncoderSettings
        {
            PixelFormat = "gbrp12le",
            BitDepth = VideoBitDepth.Eight,
            Chroma = ChromaSubsampling.Yuv420,
        };

        Assert.Equal("gbrp12le", settings.ResolvePixelFormat());
    }

    [Fact]
    public void EightBitFormatsCarryNoEndiannessSuffix()
    {
        var settings = new VideoEncoderSettings { BitDepth = VideoBitDepth.Eight, Chroma = ChromaSubsampling.Yuv444 };
        Assert.Equal("yuv444p", settings.ResolvePixelFormat());
    }

    // ---------------- subtitle burn-in ----------------

    [Fact]
    public void BurnInPrependsItselfToTheFilterChain()
    {
        // Subtitles are rendered into the picture, so anything else must filter the
        // rendered result — scaling after burn-in scales the text too.
        var composed = EncodeJobBuilder.ComposeFilters(Request() with
        {
            BurnInSubtitles = true,
            FilterScript = "scale=1280:720",
        });

        Assert.StartsWith("subtitles=", composed);
        Assert.EndsWith("scale=1280:720", composed);
    }

    [Fact]
    public void WithoutBurnInTheFilterChainIsUntouched()
    {
        var composed = EncodeJobBuilder.ComposeFilters(Request() with { FilterScript = "hqdn3d=4:3:6" });
        Assert.Equal("hqdn3d=4:3:6", composed);
    }

    [Fact]
    public void BurnInPrefersAnExternalSubtitleFile()
    {
        var composed = EncodeJobBuilder.ComposeFilters(Request() with
        {
            BurnInSubtitles = true,
            ExternalSubtitleFile = "subs.srt",
            BurnInSubtitleTrack = 2,
        });

        Assert.Contains("subs.srt", composed);
        // An external file has one stream, so there is nothing to index into.
        Assert.DoesNotContain("si=", composed);
    }

    [Fact]
    public void BurnInIndexesIntoTheSourcesOwnSubtitleStreams()
    {
        var composed = EncodeJobBuilder.ComposeFilters(Request() with
        {
            BurnInSubtitles = true,
            BurnInSubtitleTrack = 2,
        });

        Assert.Contains("si=2", composed);
    }

    [Fact]
    public void FilterPathsEscapeTheCharactersFFmpegTreatsAsSyntax()
    {
        // ':' separates filter options, so a Windows drive letter breaks the whole graph.
        var escaped = EncodeJobBuilder.EscapeForFilter(@"C:\videos\clip.srt");

        Assert.Equal(@"C\:/videos/clip.srt", escaped);
        Assert.DoesNotContain(@"\v", escaped);
    }

    // ---------------- warnings ----------------

    private static MediaFileInfo Hdr(bool dolbyVision = false, bool hdr10Plus = false, string pixelFormat = "yuv420p10le")
        => new()
        {
            FilePath = "in.mkv",
            Duration = TimeSpan.FromMinutes(1),
            Video = new VideoStreamInfo
            {
                Codec = "hevc",
                Width = 3840,
                Height = 2160,
                BitDepth = 10,
                PixelFormat = pixelFormat,
                ColorTransfer = "smpte2084",
                ColorPrimaries = "bt2020",
                HasHdr10Metadata = true,
                HasDolbyVision = dolbyVision,
                HasHdr10Plus = hdr10Plus,
            },
        };

    [Fact]
    public void HdrSourceIsDetected()
    {
        Assert.True(Hdr().Video!.IsHdr);
        Assert.False(new VideoStreamInfo { Codec = "h264" }.IsHdr);
    }

    [Fact]
    public void HlgCountsAsHdrToo()
    {
        var hlg = new VideoStreamInfo { Codec = "hevc", ColorTransfer = "arib-std-b67" };
        Assert.True(hlg.IsHdr);
    }

    [Fact]
    public void ForcingEightBitOnHdrIsWarnedAbout()
    {
        var warnings = EncodeJobBuilder.Warnings(Request() with { BitDepth = VideoBitDepth.Eight }, Hdr());

        Assert.Contains(warnings, w => w.Contains("8-bit") && w.Contains("HDR"));
    }

    [Fact]
    public void ToneMappingAnHdrSourceIsWarnedAbout()
    {
        var warnings = EncodeJobBuilder.Warnings(
            Request() with { FilterScript = "tonemap=hable" }, Hdr());

        Assert.Contains(warnings, w => w.Contains("SDR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DynamicMetadataLossIsWarnedAbout()
    {
        Assert.Contains(EncodeJobBuilder.Warnings(Request(), Hdr(dolbyVision: true)),
            w => w.Contains("Dolby Vision"));

        Assert.Contains(EncodeJobBuilder.Warnings(Request(), Hdr(hdr10Plus: true)),
            w => w.Contains("HDR10+"));
    }

    [Fact]
    public void ThrowingAwayChromaResolutionIsWarnedAbout()
    {
        var warnings = EncodeJobBuilder.Warnings(
            Request() with { Chroma = ChromaSubsampling.Yuv420 },
            Hdr(pixelFormat: "yuv444p10le"));

        Assert.Contains(warnings, w => w.Contains("4:2:0"));
    }

    [Fact]
    public void BurnInAndChunkingAreFlaggedAsIncompatible()
    {
        // Each chunk seeks independently, so subtitle timings would drift after the first.
        var warnings = EncodeJobBuilder.Warnings(
            Request() with { BurnInSubtitles = true }, null, chunked: true);

        Assert.Contains(warnings, w => w.Contains("chunk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnOrdinarySdrEncodeProducesNoWarnings()
    {
        var sdr = new MediaFileInfo
        {
            FilePath = "in.mkv",
            Video = new VideoStreamInfo { Codec = "h264", BitDepth = 8, PixelFormat = "yuv420p" },
        };

        Assert.Empty(EncodeJobBuilder.Warnings(Request(), sdr));
    }

    // ---------------- SVT-AV1 tuning ----------------

    [Theory]
    [InlineData("vq", SvtAv1Tune.Vq)]
    [InlineData("psnr", SvtAv1Tune.Psnr)]
    [InlineData("ms-ssim", SvtAv1Tune.MsSsim)]
    [InlineData("vmaf", SvtAv1Tune.Vmaf)]
    public void SvtTuneNamesParse(string raw, SvtAv1Tune expected)
        => Assert.Equal(expected, EncodeOptions.ParseSvtTune(raw));

    [Fact]
    public void SvtTuneDefaultsToLeavingTheEncoderAlone()
        => Assert.Null(EncodeOptions.ParseSvtTune(null));

    [Fact]
    public void UnknownSvtTuneIsRejected()
        => Assert.Throws<UsageException>(() => EncodeOptions.ParseSvtTune("butter"));

    [Fact]
    public void SvtTuneValuesMatchTheEncodersOwnNumbering()
    {
        // FFmpeg reports: 0 VQ, 1 PSNR, 2 SSIM, 3 IQ, 4 MS_SSIM, 5 VMAF.
        Assert.Equal(0, (int)SvtAv1Tune.Vq);
        Assert.Equal(1, (int)SvtAv1Tune.Psnr);
        Assert.Equal(5, (int)SvtAv1Tune.Vmaf);
    }

    // ---------------- chroma parsing ----------------

    [Theory]
    [InlineData("420", ChromaSubsampling.Yuv420)]
    [InlineData("4:2:2", ChromaSubsampling.Yuv422)]
    [InlineData("444", ChromaSubsampling.Yuv444)]
    [InlineData("auto", ChromaSubsampling.Auto)]
    [InlineData(null, ChromaSubsampling.Auto)]
    public void ChromaNamesParse(string? raw, ChromaSubsampling expected)
        => Assert.Equal(expected, EncodeOptions.ParseChroma(raw));

    [Fact]
    public void UnknownChromaIsRejected()
        => Assert.Throws<UsageException>(() => EncodeOptions.ParseChroma("411"));
}
