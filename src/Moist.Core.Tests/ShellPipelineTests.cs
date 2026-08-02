using System.Diagnostics;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;

namespace Moist.Core.Tests;

/// <summary>
/// The "decoder | encoder" command the standalone x264/x265 processors run.
///
/// These need no encoder installed, so they hold the line in CI where the integration
/// tests for the same code skip themselves.
/// </summary>
public class ShellPipelineTests
{
    private static VideoJob Job(Action<VideoJob>? tweak = null)
    {
        var job = new VideoJob
        {
            Name = "test",
            InputFile = "/media/in.mkv",
            OutputFile = "/media/out.mkv",
            EncoderId = "cli-x264",
            Settings = new X264Settings(),
            TotalFrames = 1000,
        };
        tweak?.Invoke(job);
        return job;
    }

    [Fact]
    public void TheWholeCommandIsHandedToTheShellIntact()
    {
        // cmd.exe removes the first and last quote of what follows /c. A pipeline starts
        // with a quoted tool path, so without an outer pair of quotes to sacrifice the
        // command arrives mangled and every encode fails — which is exactly what shipped.
        var psi = new ProcessStartInfo();
        const string command = "\"C:\\ff\\ffmpeg.exe\" -i \"in.mkv\" - | \"C:\\x264.exe\" -o \"out.mkv\" -";

        ShellPipeline.Apply(psi, command);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", psi.FileName);
            Assert.Equal($"/s /c \"{command}\"", psi.Arguments);
        }
        else
        {
            // On Unix the command travels as one argv entry and is never re-parsed.
            Assert.Equal("/bin/sh", psi.FileName);
            Assert.Equal(["-c", command], psi.ArgumentList);
        }
    }

    [Fact]
    public void PathsThatWouldSplitOrChainAreQuoted()
    {
        // '&' chains commands in cmd and backgrounds them in sh; spaces split arguments.
        var escaped = ShellPipeline.Escape("/media/a & b/入力 file.mkv");

        Assert.StartsWith(OperatingSystem.IsWindows() ? "\"" : "'", escaped);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "\"" : "'", escaped);
        Assert.Contains("a & b", escaped);
    }

    [Fact]
    public void TheDecodeSideSeeksAndLimitsWhenTheJobIsTrimmed()
    {
        // Chunked encoding, target-quality search and per-scene optimisation all encode
        // pieces of the source. Dropping the trim here made every chunk and every probe
        // encode the whole file instead.
        var command = ShellPipeline.FfmpegDecodeCommand("ffmpeg", Job(j =>
        {
            j.TrimStart = TimeSpan.FromSeconds(12.5);
            j.TrimFrames = 240;
        }));

        Assert.Contains("-ss 12.500", command);
        Assert.Contains("-frames:v 240", command);

        // -ss belongs ahead of -i so FFmpeg seeks rather than decoding and discarding.
        Assert.True(command.IndexOf("-ss", StringComparison.Ordinal) < command.IndexOf("-i ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExactFrameCountBeatsADuration()
    {
        // A duration in seconds cannot address a frame boundary at 23.976 fps, and the
        // rounding error accumulates across chunks until frames are gained or lost.
        var command = ShellPipeline.FfmpegDecodeCommand("ffmpeg", Job(j =>
        {
            j.TrimDuration = TimeSpan.FromSeconds(4);
            j.TrimFrames = 96;
        }));

        Assert.Contains("-frames:v 96", command);
        Assert.DoesNotContain("-t ", command);
    }

    [Fact]
    public void NothingIsForcedOnThePixelFormatByDefault()
    {
        // Forcing 8-bit "for safety" is how a 10-bit master gets quietly flattened.
        Assert.DoesNotContain("-pix_fmt", ShellPipeline.FfmpegDecodeCommand("ffmpeg", Job()));
    }

    [Fact]
    public void AskingForTenBitReachesTheDecodeSide()
    {
        var command = ShellPipeline.FfmpegDecodeCommand("ffmpeg", Job(j =>
            j.Settings = new X264Settings { BitDepth = VideoBitDepth.Ten }));

        Assert.Contains("-pix_fmt yuv420p10le", command);
    }

    [Theory]
    [InlineData(VideoBitDepth.Auto, 10, 10)]   // follow a 10-bit source rather than truncating it
    [InlineData(VideoBitDepth.Auto, 8, 8)]
    [InlineData(VideoBitDepth.Ten, 8, 10)]
    [InlineData(VideoBitDepth.Twelve, 8, 12)]
    public void TheEncoderIsToldTheDepthThePipeWillCarry(VideoBitDepth requested, int source, int expected)
    {
        var depth = ShellPipeline.OutputBitDepth(Job(j =>
        {
            j.Settings = new X264Settings { BitDepth = requested };
            j.SourceBitDepth = source;
        }));

        Assert.Equal(expected, depth);
    }

    [Fact]
    public void FormatNamesEndingInDigitsAreNotMistakenForADepth()
    {
        // nv12 is an 8-bit format whose name ends in "12".
        var depth = ShellPipeline.OutputBitDepth(Job(j => j.Settings = new X264Settings { PixelFormat = "nv12" }));

        Assert.Equal(8, depth);
    }

    [Fact]
    public void ProgressIsMeasuredAgainstTheFramesActuallyRequested()
    {
        // y4m carries no frame count, so the encoders cannot report a percentage
        // themselves; a trimmed job is finished at its trim, not at the source length.
        var job = Job(j => { j.TotalFrames = 1000; j.TrimFrames = 100; });

        Assert.Equal(50, ShellPipeline.PercentageOf(50, job));
    }

    [Fact]
    public void ProgressStaysAbsentWhenTheLengthIsUnknown()
    {
        Assert.Null(ShellPipeline.PercentageOf(10, Job(j => j.TotalFrames = 0)));
    }
}
