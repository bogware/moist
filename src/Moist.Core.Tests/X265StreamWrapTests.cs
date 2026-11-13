using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

/// <summary>
/// The standalone x265 encoder writes a raw HEVC stream with no timestamps. FFmpeg can only
/// number its packets in decode order, which matches display order only without B-frames — so
/// the default wrap turns B-frames off, and the mkvmerge wrap (which reads picture order) keeps
/// them. These pin which x265 arguments each mode produces, without launching anything.
/// </summary>
public class X265StreamWrapTests
{
    private sealed class Probe(bool mkvmergeWorks) : X265CliProcessor("x265", "ffmpeg", "ffprobe", "mkvmerge")
    {
        public int MkvmergeChecks { get; private set; }
        protected override bool MkvmergeWorks() { MkvmergeChecks++; return mkvmergeWorks; }
        public string Args(Job job) => BuildArguments(job);
    }

    private static VideoJob Job(X265StreamWrap wrap, string? customArgs = null) => new()
    {
        Name = "x", InputFile = "in.mkv", OutputFile = "out.mkv", EncoderId = "cli-x265",
        Settings = new X265Settings { StreamWrap = wrap, CustomArgs = customArgs ?? string.Empty },
    };

    [Fact]
    public void Default_IsNoBFrames()
        => Assert.Equal(X265StreamWrap.NoBFrames, new X265Settings().StreamWrap);

    [Fact]
    public void NoBFrames_DisablesBFrames_WithoutLookingForMkvmerge()
    {
        var p = new Probe(mkvmergeWorks: true);
        Assert.Contains("--bframes 0", p.Args(Job(X265StreamWrap.NoBFrames)));
        Assert.Equal(0, p.MkvmergeChecks);
    }

    [Fact]
    public void Mkvmerge_KeepsBFrames()
        => Assert.DoesNotContain("--bframes", new Probe(mkvmergeWorks: true).Args(Job(X265StreamWrap.Mkvmerge)));

    [Fact]
    public void Mkvmerge_RefusesUpFront_WhenMkvmergeIsMissing()
    {
        // Better to fail before encoding for an hour than to discover it at the wrap.
        var ex = Assert.Throws<InvalidOperationException>(() => new Probe(mkvmergeWorks: false).Args(Job(X265StreamWrap.Mkvmerge)));
        Assert.Contains("mkvmerge", ex.Message);
        Assert.Contains("No B-frames", ex.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Auto_UsesMkvmergeWhenAvailable_OtherwiseNoBFrames(bool available, bool expectBFramesOff)
        => Assert.Equal(expectBFramesOff, new Probe(available).Args(Job(X265StreamWrap.Auto)).Contains("--bframes 0"));

    [Fact]
    public void TheDecisionIsMadeOnce_SoBothPassesOfATwoPassEncodeAgree()
    {
        var p = new Probe(mkvmergeWorks: true);
        var job = Job(X265StreamWrap.Auto);
        var first = p.Args(job);
        var second = p.Args(job);
        Assert.Equal(first, second);
        Assert.Equal(1, p.MkvmergeChecks);
    }

    [Fact]
    public void CustomArgs_ComeAfter_SoAnExplicitBFrameCountStillWins()
    {
        var args = new Probe(false).Args(Job(X265StreamWrap.NoBFrames, "--bframes 4"));
        Assert.True(args.IndexOf("--bframes 0", StringComparison.Ordinal) < args.IndexOf("--bframes 4", StringComparison.Ordinal));
    }
}
