using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;
using Moist.Core.Media;

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

        // The superres filter upscales via libplacebo, which needs a Vulkan device declared before
        // the input — even when the encoder is a GPU one (NVENC filters on Vulkan, encodes on its
        // own engine). Skipped for VA-API, whose filter chain already binds the render device.
        if (_vendor != HardwareVendor.Vaapi && GpuShaders.UsedInFilterScript(vj.FilterScript))
            args.AddRange(["-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk"]);

        // F6, not F3: chunk starts are frame boundaries, and at odd rates (23.976 fps)
        // millisecond rounding seeks to the wrong frame, dropping the boundary frame from
        // this chunk and duplicating it in the next while the total count still checks out.
        if (vj.TrimStart is { } trimStart)
            args.AddRange(["-ss", trimStart.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture)]);

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
        {
            // nv12 is 8-bit 4:2:0, so hardcoding it silently downconverts a 10-bit/HDR
            // source before upload. Pick the surface format from the effective depth so a
            // 10-bit encode stays 10-bit; the 8-bit path is unchanged.
            var depth = s.BitDepth switch
            {
                VideoBitDepth.Eight => 8,
                VideoBitDepth.Ten => 10,
                VideoBitDepth.Twelve => 12,
                _ => vj.SourceBitDepth is 10 or 12 ? vj.SourceBitDepth : 8,
            };
            // VA-API 10-bit surfaces use p010; 8-bit use nv12. (12-bit VA-API is rare; p010 covers 10-bit.)
            filters.Add(depth >= 10 ? "format=p010,hwupload" : "format=nv12,hwupload");
        }
        if (filters.Count > 0)
            args.AddRange(["-vf", Quote(string.Join(",", filters))]);

        args.AddRange(["-c:v", _ffmpegEncoderName]);

        args.AddRange(BuildRateControl(s));
        args.AddRange(BuildPreset(s));

        // Pixel format. VA-API already handled it in the filter chain above. ResolvePixelFormat
        // returns the software (libx264/libx265) names, which the GPU encoders reject — translate
        // to the vendor surface format first.
        if (_vendor != HardwareVendor.Vaapi && s.ResolvePixelFormat(vj.SourceBitDepth) is { } pixFmt)
            args.AddRange(["-pix_fmt", ToHardwarePixelFormat(pixFmt)]);

        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        args.Add("-an");
        args.Add(Quote(vj.OutputFile));

        return string.Join(" ", args);
    }

    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        // superres references its GLSL shader by bare filename; resolve it by running FFmpeg from
        // the shaders folder rather than escaping a drive-lettered path into the filter graph.
        if (job is VideoJob vj && GpuShaders.UsedInFilterScript(vj.FilterScript))
            psi.WorkingDirectory = GpuShaders.Directory;
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

    /// <summary>
    /// Map the software planar pixel-format name <see cref="VideoEncoderSettings.ResolvePixelFormat"/>
    /// returns (yuv420p10le, ...) to the surface format the GPU encoders accept (nv12/p010le/...).
    /// The 8/10-bit 4:2:0 mapping is verified on NVENC (RTX 4080); QSV, AMF and VideoToolbox take
    /// the same documented formats but are unproven here — the same "listed is not working" stance
    /// as the rest of the hardware path.
    /// </summary>
    internal static string ToHardwarePixelFormat(string pixFmt) => pixFmt switch
    {
        "yuv420p" => "nv12",
        "yuv420p10le" => "p010le",
        "yuv420p12le" => "p016le",
        "yuv444p10le" => "yuv444p16le",
        "yuv444p12le" => "yuv444p16le",
        _ => pixFmt, // yuv444p (8-bit) and anything else pass through unchanged
    };

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
