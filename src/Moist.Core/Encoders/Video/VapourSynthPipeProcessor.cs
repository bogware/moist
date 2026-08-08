using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Encodes a VapourSynth-filtered job by piping <c>vspipe</c> into FFmpeg: the generated <c>.vpy</c>
/// is the whole source + filter graph (including vs-mlrt ML nodes like Real-ESRGAN via ONNX/TensorRT),
/// and FFmpeg consumes its y4m output and encodes with the job's settings.
///
/// This closes the gap CLAUDE.md called out — "nothing on the encode path reads FilterEngineId".
/// A job whose <see cref="VideoJob.FilterEngineId"/> is "vapoursynth" is piped here instead of being
/// handed a <c>-vf</c> string that FFmpeg can't run. It reuses <see cref="ShellPipeline"/>, the same
/// producer|consumer machinery behind the standalone x264/x265 encoders.
/// </summary>
public sealed partial class VapourSynthPipeProcessor : CommandLineProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _vspipePath;

    public VapourSynthPipeProcessor(string ffmpegPath, string vspipePath)
    {
        _ffmpegPath = ffmpegPath;
        _vspipePath = vspipePath;
    }

    public override string Id => "vapoursynth-pipe";
    public override string DisplayName => "VapourSynth → FFmpeg";

    public override bool CanProcess(Job job)
        => job is VideoJob vj
           && string.Equals(vj.FilterEngineId, "vapoursynth", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(vj.FilterScript);

    // The real command is a shell pipeline set in OnBeforeStart; this is just the fallback exe.
    protected override string GetExecutablePath(Job job) => _ffmpegPath;

    /// <summary>The FFmpeg (consumer) half of the pipe — used to compose the pipeline in OnBeforeStart.</summary>
    protected override string BuildArguments(Job job) => string.Join(" ", BuildConsumerArgs((VideoJob)job));

    /// <summary>FFmpeg args that read y4m on stdin and encode to the video temp. Pure, for tests.</summary>
    internal static List<string> BuildConsumerArgs(VideoJob vj)
    {
        var s = vj.Settings;
        var args = new List<string>
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-f", "yuv4mpegpipe", "-i", "-",
            "-c:v", RealEsrganVideoProcessor.FinalCodec(vj.EncoderId),
            // CRF/quality is the natural rate control here; other modes fall back to CRF in this path.
            "-crf", s.Quality.ToString("0.##", CultureInfo.InvariantCulture),
            "-preset", s.Preset.ToString().ToLowerInvariant(),
        };
        if (s.Tune != VideoTune.None)
            args.AddRange(["-tune", s.Tune.ToString().ToLowerInvariant()]);
        // The .vpy already set the format; only force one when the user explicitly chose a depth.
        if (s.ResolvePixelFormat(vj.SourceBitDepth) is { } pix)
            args.AddRange(["-pix_fmt", pix]);
        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);
        args.Add("-an");
        args.Add(ShellPipeline.Escape(vj.OutputFile));
        return args;
    }

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        var vj = (VideoJob)job;

        // Write the generated .vpy beside the output; vspipe opens the source and runs the graph.
        var dir = Path.GetDirectoryName(Path.GetFullPath(vj.OutputFile))!;
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, $"{job.Id}.vpy");
        File.WriteAllText(scriptPath, vj.FilterScript!);

        var producer = $"{ShellPipeline.Escape(_vspipePath)} --y4m {ShellPipeline.Escape(scriptPath)} -";
        var consumer = $"{ShellPipeline.Escape(_ffmpegPath)} {BuildArguments(job)}";
        ShellPipeline.Apply(psi, $"{producer} | {consumer}");
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        var m = FrameRegex().Match(line);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var frame)) return null;

        var vj = (VideoJob)job;
        var total = vj.TotalFrames;
        return new JobProgress
        {
            FramesProcessed = frame,
            TotalFrames = total,
            Percentage = total > 0 ? Math.Clamp(frame * 100.0 / total, 0, 100) : 0,
            StatusLine = line.Trim(),
        };
    }

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameRegex();
}
