using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Drives a GPU encoder through FFmpeg.
///
/// This is separate from <see cref="FFmpegVideoProcessor"/> because almost nothing
/// transfers: hardware encoders reject <c>-crf</c>, use their own preset scales, and
/// each vendor spells rate control differently. Trying to share one argument builder
/// produces a tangle of conditionals that is wrong for everybody.
/// </summary>
public sealed partial class FFmpegHardwareVideoProcessor : CommandLineProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _encoderId;
    private readonly string _ffmpegEncoderName;
    private readonly HardwareVendor _vendor;

    private long _totalFrames;
    private long _lastFrame;
    private double _lastFps;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => _encoderId;
    public override string DisplayName => $"{_vendor} ({_ffmpegEncoderName})";

    public FFmpegHardwareVideoProcessor(string ffmpegPath, string encoderId, string ffmpegEncoderName, HardwareVendor vendor)
    {
        _ffmpegPath = ffmpegPath;
        _encoderId = encoderId;
        _ffmpegEncoderName = ffmpegEncoderName;
        _vendor = vendor;
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
        var hw = s as HardwareVideoSettings;

        var args = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };

        // VA-API needs the render device selected before the input is opened.
        if (_vendor == HardwareVendor.Vaapi)
            args.AddRange(["-vaapi_device", hw?.VaapiDevice ?? "/dev/dri/renderD128"]);

        if (vj.TrimStart is { } trimStart)
            args.AddRange(["-ss", trimStart.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);

        args.AddRange(["-i", Quote(vj.InputFile)]);

        // A frame count is exact where a duration in seconds is not, so it wins.
        if (vj.TrimFrames is { } frames)
            args.AddRange(["-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
        else if (vj.TrimDuration is { } trimDuration)
            args.AddRange(["-t", trimDuration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)]);

        // Filters. VA-API encodes from GPU memory, so whatever the user asked for has to
        // be followed by a conversion and upload.
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(vj.FilterScript))
            filters.Add(vj.FilterScript);
        if (_vendor == HardwareVendor.Vaapi)
            filters.Add("format=nv12,hwupload");
        if (filters.Count > 0)
            args.AddRange(["-vf", Quote(string.Join(",", filters))]);

        args.AddRange(["-c:v", _ffmpegEncoderName]);

        args.AddRange(BuildRateControl(s));
        args.AddRange(BuildPreset(s));

        // Pixel format. VA-API already handled it in the filter chain above.
        if (_vendor != HardwareVendor.Vaapi && s.ResolvePixelFormat() is { } pixFmt)
            args.AddRange(["-pix_fmt", pixFmt]);

        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        args.Add("-an");
        args.Add(Quote(vj.OutputFile));

        return string.Join(" ", args);
    }

    /// <summary>
    /// Translate moist's mode + quality into the vendor's rate control.
    /// The quality scale is QP-like (0-51) for every vendor except VideoToolbox.
    /// </summary>
    private string[] BuildRateControl(VideoEncoderSettings s)
    {
        var q = (int)Math.Round(Math.Clamp(s.Quality, 0, 51));
        var bitrate = $"{s.Bitrate}k";
        var constantQuality = s.Mode is VideoEncodingMode.ConstantRateFactor or VideoEncodingMode.ConstantQuality;
        var twoPass = s.Mode == VideoEncodingMode.TwoPassBitrate;
        var hw = s as HardwareVideoSettings;

        return _vendor switch
        {
            HardwareVendor.Nvenc when constantQuality =>
                // Constant-quality VBR: -b:v 0 hands full control to -cq.
                ["-rc", "vbr", "-cq", q.ToString(), "-b:v", "0"],

            HardwareVendor.Nvenc =>
                [
                    "-rc", "vbr", "-b:v", bitrate,
                    // NVENC has no -pass; a second analysis pass is requested like this.
                    .. twoPass && (hw?.NvencMultipass ?? true) ? new[] { "-multipass", "fullres" } : [],
                ],

            HardwareVendor.Qsv when constantQuality =>
                ["-global_quality", q.ToString()],

            HardwareVendor.Qsv =>
                ["-b:v", bitrate],

            HardwareVendor.Amf when constantQuality =>
                ["-rc", "cqp", "-qp_i", q.ToString(), "-qp_p", q.ToString(), "-qp_b", q.ToString()],

            HardwareVendor.Amf =>
                ["-rc", "vbr_peak", "-b:v", bitrate],

            // VideoToolbox scores quality 0-100, ascending. Invert the QP-style scale.
            HardwareVendor.VideoToolbox when constantQuality =>
                ["-q:v", QpToVideoToolboxQuality(q).ToString(CultureInfo.InvariantCulture), "-b:v", "0"],

            HardwareVendor.VideoToolbox =>
                ["-b:v", bitrate],

            HardwareVendor.Vaapi when constantQuality =>
                ["-rc_mode", "CQP", "-qp", q.ToString()],

            HardwareVendor.Vaapi =>
                ["-rc_mode", "VBR", "-b:v", bitrate],

            _ => ["-b:v", bitrate],
        };
    }

    /// <summary>0-51 QP (lower is better) onto VideoToolbox's 0-100 (higher is better).</summary>
    internal static int QpToVideoToolboxQuality(int qp)
        => Math.Clamp((int)Math.Round((51 - qp) / 51.0 * 100), 1, 100);

    private string[] BuildPreset(VideoEncoderSettings s)
    {
        var hw = s as HardwareVideoSettings;
        var quality = hw?.TuneForQuality ?? true;

        return _vendor switch
        {
            // p1 fastest .. p7 slowest.
            HardwareVendor.Nvenc =>
            [
                "-preset", NvencPreset(s.Preset),
                .. quality ? new[] { "-tune", "hq" } : [],
            ],

            HardwareVendor.Qsv => ["-preset", QsvPreset(s.Preset)],

            HardwareVendor.Amf => ["-quality", AmfQuality(s.Preset)],

            // Neither VideoToolbox nor VA-API expose a speed/quality preset.
            _ => [],
        };
    }

    internal static string NvencPreset(VideoPreset preset) => preset switch
    {
        VideoPreset.Ultrafast or VideoPreset.Superfast => "p1",
        VideoPreset.Veryfast => "p2",
        VideoPreset.Faster => "p3",
        VideoPreset.Fast or VideoPreset.Medium => "p4",
        VideoPreset.Slow => "p5",
        VideoPreset.Slower => "p6",
        VideoPreset.Veryslow or VideoPreset.Placebo => "p7",
        _ => "p4",
    };

    internal static string QsvPreset(VideoPreset preset) => preset switch
    {
        VideoPreset.Ultrafast or VideoPreset.Superfast => "veryfast",
        VideoPreset.Veryfast => "faster",
        VideoPreset.Faster => "fast",
        VideoPreset.Fast or VideoPreset.Medium => "medium",
        VideoPreset.Slow => "slow",
        VideoPreset.Slower => "slower",
        VideoPreset.Veryslow or VideoPreset.Placebo => "veryslow",
        _ => "medium",
    };

    internal static string AmfQuality(VideoPreset preset) => preset switch
    {
        VideoPreset.Ultrafast or VideoPreset.Superfast or VideoPreset.Veryfast => "speed",
        VideoPreset.Faster or VideoPreset.Fast or VideoPreset.Medium => "balanced",
        _ => "quality",
    };

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        var frameMatch = FrameRegex().Match(line);
        if (frameMatch.Success && long.TryParse(frameMatch.Groups[1].Value, out var frame))
            _lastFrame = frame;

        var fpsMatch = FpsRegex().Match(line);
        if (fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var fps))
            _lastFps = fps;

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
                StatusLine = line.Trim(),
            };
        }

        return null;
    }

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"fps=\s*([\d.]+)")]
    private static partial Regex FpsRegex();
}
