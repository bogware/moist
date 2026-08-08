using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;
using Moist.Core.Media;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// FFmpeg-based video encoder processor. Supports x264, x265, SVT-AV1, and more
/// by building the appropriate FFmpeg command line.
/// </summary>
public sealed partial class FFmpegVideoProcessor : CommandLineProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _codec;
    private long _totalFrames;
    private long _lastFrame;
    private double _lastFps;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => $"ffmpeg-{_codec}";
    public override string DisplayName => $"FFmpeg ({_codec})";

    public FFmpegVideoProcessor(string ffmpegPath, string codec)
    {
        _ffmpegPath = ffmpegPath;
        _codec = codec;
    }

    public override bool CanProcess(Job job)
        => job is VideoJob vj && vj.EncoderId == Id;

    protected override string GetExecutablePath(Job job) => _ffmpegPath;

    protected override string BuildArguments(Job job)
    {
        var vj = (VideoJob)job;
        if (vj.TotalFrames > 0) _totalFrames = vj.TotalFrames;
        _lastFrame = 0;
        _lastFps = 0;
        _stopwatch.Reset();
        var s = vj.Settings;
        var args = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };

        // The superres filter upscales on the GPU via libplacebo, which needs a Vulkan device
        // declared before the input. Detect it by the presence of "libplacebo" in the chain.
        if (UsesLibplacebo(vj))
            args.AddRange(["-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk"]);

        // Seek before -i so FFmpeg skips ahead instead of decoding and discarding.
        if (vj.TrimStart is { } trimStart)
            args.AddRange(["-ss", trimStart.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);

        args.AddRange(["-i", Quote(vj.InputFile)]);

        // A frame count is exact where a duration in seconds is not, so it wins.
        if (vj.TrimFrames is { } frames)
            args.AddRange(["-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
        else if (vj.TrimDuration is { } trimDuration)
            args.AddRange(["-t", trimDuration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);

        // Apply filter script if present
        if (!string.IsNullOrWhiteSpace(vj.FilterScript))
        {
            // FFmpeg filter engine produces simple linear chains (e.g. "scale=1920:1080,hqdn3d=4:3:6")
            // which use -vf. VapourSynth/AviSynth scripts are not directly supported by FFmpeg CLI
            // and require piping through vspipe/avs2pipe (handled at the job creation level).
            args.AddRange(["-vf", Quote(vj.FilterScript)]);
        }

        // Codec selection
        args.AddRange(["-c:v", GetFfmpegCodecName()]);

        // Encoding mode
        switch (s.Mode)
        {
            case VideoEncodingMode.ConstantRateFactor:
                args.AddRange(GetCrfArgs(s));
                break;
            case VideoEncodingMode.ConstantQuality:
                args.AddRange(GetQualityArgs(s));
                break;
            case VideoEncodingMode.SinglePassBitrate:
                args.AddRange(["-b:v", $"{s.Bitrate}k"]);
                break;
            case VideoEncodingMode.TwoPassBitrate:
                // Handled externally via two-pass logic
                args.AddRange(["-b:v", $"{s.Bitrate}k"]);
                break;
        }

        // Preset
        args.AddRange(GetPresetArgs(s));

        // Tune (skip for SVT-AV1 which doesn't support -tune)
        if (s.Tune != VideoTune.None && _codec != "svtav1")
            args.AddRange(["-tune", s.Tune.ToString().ToLowerInvariant()]);

        // Pixel format. Left unset by default so the encoder follows the source —
        // forcing 8-bit would silently discard precision from a 10-bit master.
        if (s.ResolvePixelFormat(vj.SourceBitDepth) is { } pixFmt)
            args.AddRange(["-pix_fmt", pixFmt]);

        // Codec-specific settings
        args.AddRange(GetCodecSpecificArgs(s));

        // Custom args
        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        // No audio in video-only encode
        args.Add("-an");

        args.Add(Quote(vj.OutputFile));

        return string.Join(" ", args);
    }

    private static bool UsesLibplacebo(VideoJob vj) => GpuShaders.UsedInFilterScript(vj.FilterScript);

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        // superres references its GLSL shader by bare filename; resolve it by running FFmpeg from
        // the shaders folder rather than escaping a drive-lettered path into the filter graph.
        if (job is VideoJob vj && UsesLibplacebo(vj))
            psi.WorkingDirectory = GpuShaders.Directory;
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        // Track frame and fps from both formats:
        // stderr status: "frame= 1234 fps= 45.6 ..." (single line)
        // -progress pipe:1: "frame=1234\n" and "fps=45.6\n" (separate lines)
        var frameMatch = FrameRegex().Match(line);
        if (frameMatch.Success && long.TryParse(frameMatch.Groups[1].Value, out var frame))
            _lastFrame = frame;

        var fpsMatch = FpsRegex().Match(line);
        if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var fps))
            _lastFps = fps;

        // Report progress when we have a new frame number
        if (frameMatch.Success && _lastFrame > 0)
        {
            var pct = _totalFrames > 0 ? (double)_lastFrame / _totalFrames * 100 : 0;
            var eta = _lastFps > 0 && _totalFrames > 0
                ? TimeSpan.FromSeconds((_totalFrames - _lastFrame) / _lastFps)
                : TimeSpan.Zero;

            return new JobProgress
            {
                FramesProcessed = _lastFrame,
                TotalFrames = _totalFrames,
                Percentage = pct,
                Fps = _lastFps,
                Elapsed = _stopwatch.Elapsed,
                Eta = eta,
                StatusLine = line.Trim()
            };
        }

        return null;
    }

    private string GetFfmpegCodecName() => _codec switch
    {
        "x264" => "libx264",
        "x265" => "libx265",
        "svtav1" => "libsvtav1",
        "vp9" => "libvpx-vp9",
        "ffv1" => "ffv1",
        "huffyuv" => "huffyuv",
        _ => _codec
    };

    private static string[] GetCrfArgs(VideoEncoderSettings s)
        => ["-crf", s.Quality.ToString("F1", CultureInfo.InvariantCulture)];

    private static string[] GetQualityArgs(VideoEncoderSettings s)
        => ["-q:v", s.Quality.ToString("F0", CultureInfo.InvariantCulture)];

    private string[] GetPresetArgs(VideoEncoderSettings s)
    {
        if (_codec == "svtav1" && s is SvtAv1Settings svt)
            return ["-preset", svt.SvtPreset.ToString()];

        return ["-preset", s.Preset.ToString().ToLowerInvariant()];
    }

    // A profile is only sent when the user asked for one. Deriving it ourselves means
    // guessing the encoder's internal bit depth, and guessing wrong is a hard failure.
    private string[] GetCodecSpecificArgs(VideoEncoderSettings s) => s switch
    {
        X264Settings x264 =>
        [
            .. x264.Profile is not null ? new[] { "-profile:v", x264.Profile } : [],
            .. x264.Level is not null ? new[] { "-level", x264.Level } : [],
            .. x264.BlurayCompat ? new[] { "-bluray-compat", "1" } : [],
        ],
        X265Settings x265 =>
        [
            .. x265.Profile is not null ? new[] { "-profile:v", x265.Profile } : [],
            .. x265.Level is not null ? new[] { "-level", x265.Level } : [],
            .. x265.Hdr ? new[] { "-tag:v", "hvc1" } : [],
        ],
        SvtAv1Settings svt => BuildSvtAv1Args(svt),
        _ => []
    };

    /// <summary>
    /// SVT-AV1 takes its own options as one colon-separated blob, so they have to be
    /// gathered rather than emitted as separate flags.
    /// </summary>
    private static string[] BuildSvtAv1Args(SvtAv1Settings svt)
    {
        var parameters = new List<string>();

        if (svt.SvtTune is { } tune)
            parameters.Add($"tune={(int)tune}");

        if (svt.FilmGrain > 0)
        {
            parameters.Add($"film-grain={svt.FilmGrain}");
            parameters.Add($"film-grain-denoise={svt.FilmGrainDenoise}");
        }

        return parameters.Count == 0 ? [] : ["-svtav1-params", string.Join(":", parameters)];
    }

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"fps=\s*([\d.]+)")]
    private static partial Regex FpsRegex();

}
