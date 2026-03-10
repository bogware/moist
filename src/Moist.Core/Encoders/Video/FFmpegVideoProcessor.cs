using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

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
        var args = new List<string>
        {
            "-y", "-hide_banner", "-progress", "pipe:1",
            "-i", Quote(vj.InputFile)
        };

        // Apply filter script if present
        if (!string.IsNullOrWhiteSpace(vj.FilterScript))
        {
            args.AddRange(["-filter_complex", Quote(vj.FilterScript)]);
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

        // Pixel format
        if (s.PixelFormat is not null)
            args.AddRange(["-pix_fmt", s.PixelFormat]);

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

    private string[] GetCodecSpecificArgs(VideoEncoderSettings s) => s switch
    {
        X264Settings x264 =>
        [
            "-profile:v", x264.Profile,
            .. x264.Level is not null ? new[] { "-level", x264.Level } : [],
            .. x264.BlurayCompat ? new[] { "-bluray-compat", "1" } : [],
        ],
        X265Settings x265 =>
        [
            "-profile:v", x265.Profile,
            .. x265.Level is not null ? new[] { "-level", x265.Level } : [],
            .. x265.Hdr ? new[] { "-tag:v", "hvc1" } : [],
        ],
        SvtAv1Settings svt =>
        [
            .. svt.FilmGrain > 0
                ? new[] { "-svtav1-params", $"film-grain={svt.FilmGrain}:film-grain-denoise={svt.FilmGrainDenoise}" }
                : [],
        ],
        _ => []
    };

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"fps=\s*([\d.]+)")]
    private static partial Regex FpsRegex();

}
