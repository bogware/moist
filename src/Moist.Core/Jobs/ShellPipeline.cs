using System.Diagnostics;
using System.Globalization;
using Moist.Core.Encoders.Video;

namespace Moist.Core.Jobs;

/// <summary>
/// Command lines of the form "decoder | encoder".
///
/// The standalone x264 and x265 binaries read raw frames on stdin rather than opening a
/// container themselves, so FFmpeg decodes the source into a y4m pipe and the encoder
/// consumes it. Running a pipe means going through a shell, and the two shells have to be
/// handed the command in quite different ways — see <see cref="Apply"/>.
/// </summary>
public static class ShellPipeline
{
    /// <summary>Quote a path or argument for interpolation into a pipeline.</summary>
    public static string Escape(string value)
    {
        if (!OperatingSystem.IsWindows())
            return "'" + value.Replace("'", "'\\''") + "'";

        // cmd.exe has no escape for a double quote inside a quoted argument (\" is a C-runtime
        // convention cmd itself ignores), so one would end the quoting and let the rest of the
        // value run as shell syntax. Windows paths can't contain '"', and validated filter
        // values can't either, so refusing it costs nothing legitimate. Newlines would end the
        // command outright.
        if (value.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException($"refusing to pass a value containing a double quote or newline through cmd.exe: {value}");
        return "\"" + value + "\"";
    }

    /// <summary>
    /// Point a <see cref="ProcessStartInfo"/> at a shell that will run <paramref name="command"/>.
    /// </summary>
    public static void Apply(ProcessStartInfo psi, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";

            // cmd.exe strips the first and last quote of everything following /c when the
            // string starts with a quote. A pipeline always starts with a quoted tool path,
            // so `/c "ffmpeg" -i in | "x264" -o out` reaches the parser as
            // `ffmpeg" -i in | "x264` and dies — historically with an *empty* error, because
            // what failed was the mangled first token rather than the encoder.
            //
            // Wrapping the whole command in one more pair gives cmd the pair it wants to
            // remove and leaves the pipeline intact. /s makes that removal unconditional
            // instead of contingent on how the rest of the line happens to be quoted.
            psi.Arguments = $"/s /c \"{command}\"";
            return;
        }

        // On Unix .NET hands argv to execve itself, so the command travels as a single
        // argument and never gets re-parsed on the way. Arguments and ArgumentList are
        // mutually exclusive, hence the clear.
        psi.FileName = "/bin/sh";
        psi.Arguments = string.Empty;
        psi.ArgumentList.Clear();
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
    }

    /// <summary>
    /// The FFmpeg half of the pipe: decode the source to y4m on stdout, honouring the
    /// trim, filter and pixel-format settings on the job.
    ///
    /// The trims matter beyond user-facing "encode this section": chunked encoding,
    /// target-quality search and per-scene optimisation all work by encoding pieces of the
    /// source. An encoder that ignored them would quietly encode the whole file for every
    /// chunk and every probe.
    /// </summary>
    public static string FfmpegDecodeCommand(string ffmpegPath, VideoJob vj)
    {
        var parts = new List<string> { Escape(ffmpegPath), "-y", "-hide_banner" };

        // Before -i, so FFmpeg seeks instead of decoding and discarding. F6, not F3: chunk
        // starts are frame boundaries and at odd rates (23.976 fps) millisecond rounding seeks
        // to the wrong frame, so the boundary frame is dropped from this chunk and duplicated in
        // the next — invisible to the total frame-count check.
        if (vj.TrimStart is { } start)
            parts.AddRange(["-ss", start.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture)]);

        parts.AddRange(["-i", Escape(vj.InputFile)]);

        // A frame count is exact where a duration in seconds is not, so it wins.
        if (vj.TrimFrames is { } frames)
            parts.AddRange(["-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
        else if (vj.TrimDuration is { } duration)
            parts.AddRange(["-t", duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);

        if (!string.IsNullOrWhiteSpace(vj.FilterScript))
            parts.AddRange(["-vf", Escape(vj.FilterScript)]);

        // Depth and chroma are decided here rather than by the encoder, because by the time
        // frames reach x264 the pipe has already fixed the format.
        if (vj.Settings.ResolvePixelFormat(vj.SourceBitDepth) is { } pixelFormat)
            parts.AddRange(["-pix_fmt", pixelFormat]);

        // -strict -1 lets y4m carry frame rates and formats its spec doesn't bless,
        // which is most real-world content.
        parts.AddRange(["-f", "yuv4mpegpipe", "-strict", "-1", "-"]);

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Bit depth the pipe will actually deliver, so the encoder can be told to match.
    /// </summary>
    public static int OutputBitDepth(VideoJob vj)
    {
        var pixelFormat = vj.Settings.ResolvePixelFormat(vj.SourceBitDepth);
        if (pixelFormat is null) return vj.SourceBitDepth;

        // "yuv420p10le" -> 10, "yuv444p" -> 8. Reading the plane marker rather than the
        // trailing digits, because "nv12" is an 8-bit format whose name ends in 12.
        var marker = pixelFormat.LastIndexOf('p');
        if (marker < 0 || marker == pixelFormat.Length - 1) return 8;

        var digits = new string(pixelFormat[(marker + 1)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var depth) ? depth : 8;
    }

    /// <summary>
    /// Percentage for encoders reading a pipe. y4m has no frame count in its header, so
    /// x264 and x265 cannot print one themselves — they only ever report frames done.
    /// </summary>
    public static double? PercentageOf(long framesDone, VideoJob vj)
    {
        var total = vj.TrimFrames ?? vj.TotalFrames;
        if (total <= 0) return null;
        return Math.Clamp(framesDone * 100.0 / total, 0, 100);
    }
}
