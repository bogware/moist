using System.Globalization;
using System.Text;
using Moist.Core.Media;

namespace Moist.Filters.Engines;

/// <summary>
/// FFmpeg filter engine — the universal cross-platform filter backend.
/// Translates FilterChain into FFmpeg -filter_complex syntax.
/// </summary>
public sealed class FFmpegFilterEngine : IFilterEngine
{
    public string Id => "ffmpeg";
    public string Name => "FFmpeg Filters";
    public bool IsAvailable => true; // FFmpeg is always required

    public IReadOnlyList<FilterDefinition> GetAvailableFilters() =>
    [
        // Crop
        new FilterDefinition
        {
            Id = "crop", Name = "Crop", Category = FilterCategory.Crop,
            Description = "Crop the video to a specific region",
            Parameters = new()
            {
                ["left"] = new() { Name = "Left", Type = FilterParameterType.Int, DefaultValue = 0, MinValue = 0 },
                ["top"] = new() { Name = "Top", Type = FilterParameterType.Int, DefaultValue = 0, MinValue = 0 },
                ["right"] = new() { Name = "Right", Type = FilterParameterType.Int, DefaultValue = 0, MinValue = 0 },
                ["bottom"] = new() { Name = "Bottom", Type = FilterParameterType.Int, DefaultValue = 0, MinValue = 0 },
            }
        },
        // Resize
        new FilterDefinition
        {
            Id = "resize", Name = "Resize", Category = FilterCategory.Resize,
            Description = "Scale the video to a target resolution",
            Parameters = new()
            {
                ["width"] = new() { Name = "Width", Type = FilterParameterType.Int, DefaultValue = -1 },
                ["height"] = new() { Name = "Height", Type = FilterParameterType.Int, DefaultValue = -1 },
                ["algorithm"] = new()
                {
                    Name = "Algorithm", Type = FilterParameterType.Choice, DefaultValue = "lanczos",
                    Options = ["bilinear", "bicubic", "lanczos", "spline", "neighbor"]
                },
            }
        },
        // AI / GPU super-resolution — a neural GLSL shader run through libplacebo.
        new FilterDefinition
        {
            Id = "superres", Name = "AI Upscale (GPU)", Category = FilterCategory.Resize,
            Description = "GPU neural-network upscale via libplacebo — FSRCNNX for live action, "
                          + "Anime4K for anime. Preserves aspect (targets a height). Falls back to "
                          + "a spline scale when no Vulkan GPU is present. Run it after de-block/denoise.",
            Parameters = new()
            {
                ["model"] = new()
                {
                    Name = "Model", Type = FilterParameterType.Choice, DefaultValue = "fsrcnnx",
                    Options = ["fsrcnnx", "anime4k"]
                },
                ["height"] = new() { Name = "Target Height", Type = FilterParameterType.Int, DefaultValue = 1080, MinValue = 240, MaxValue = 4320 },
            }
        },
        // Deinterlace
        new FilterDefinition
        {
            Id = "yadif", Name = "Yadif Deinterlace", Category = FilterCategory.Deinterlace,
            Description = "Yet Another DeInterlacing Filter",
            Parameters = new()
            {
                ["mode"] = new()
                {
                    Name = "Mode", Type = FilterParameterType.Choice, DefaultValue = "send_frame",
                    Options = ["send_frame", "send_field", "send_frame_nospatial", "send_field_nospatial"]
                },
                ["parity"] = new()
                {
                    Name = "Parity", Type = FilterParameterType.Choice, DefaultValue = "auto",
                    Options = ["tff", "bff", "auto"]
                },
            }
        },
        new FilterDefinition
        {
            Id = "bwdif", Name = "Bwdif Deinterlace", Category = FilterCategory.Deinterlace,
            Description = "Bob Weaver Deinterlacing Filter (better quality than yadif)",
            Parameters = new()
            {
                ["mode"] = new()
                {
                    Name = "Mode", Type = FilterParameterType.Choice, DefaultValue = "send_frame",
                    Options = ["send_frame", "send_field"]
                },
            }
        },
        // Denoise
        new FilterDefinition
        {
            Id = "nlmeans", Name = "NLMeans Denoise", Category = FilterCategory.Denoise,
            Description = "Non-local means denoiser",
            Parameters = new()
            {
                ["strength"] = new() { Name = "Strength", Type = FilterParameterType.Double, DefaultValue = 3.0, MinValue = 0.0, MaxValue = 30.0 },
                ["patch_size"] = new() { Name = "Patch Size", Type = FilterParameterType.Int, DefaultValue = 7, MinValue = 0, MaxValue = 99 },
                ["search_size"] = new() { Name = "Search Size", Type = FilterParameterType.Int, DefaultValue = 15, MinValue = 0, MaxValue = 99 },
            }
        },
        new FilterDefinition
        {
            Id = "hqdn3d", Name = "HQDN3D Denoise", Category = FilterCategory.Denoise,
            Description = "High quality 3D denoiser",
            Parameters = new()
            {
                ["luma_spatial"] = new() { Name = "Luma Spatial", Type = FilterParameterType.Double, DefaultValue = 4.0, MinValue = 0.0, MaxValue = 30.0 },
                ["chroma_spatial"] = new() { Name = "Chroma Spatial", Type = FilterParameterType.Double, DefaultValue = 3.0, MinValue = 0.0, MaxValue = 30.0 },
                ["luma_temporal"] = new() { Name = "Luma Temporal", Type = FilterParameterType.Double, DefaultValue = 6.0, MinValue = 0.0, MaxValue = 30.0 },
            }
        },
        // Sharpen
        new FilterDefinition
        {
            Id = "unsharp", Name = "Unsharp Mask", Category = FilterCategory.Sharpen,
            Description = "Sharpen or blur the video",
            Parameters = new()
            {
                ["luma_x"] = new() { Name = "Luma Matrix X", Type = FilterParameterType.Int, DefaultValue = 5, MinValue = 3, MaxValue = 23 },
                ["luma_y"] = new() { Name = "Luma Matrix Y", Type = FilterParameterType.Int, DefaultValue = 5, MinValue = 3, MaxValue = 23 },
                ["luma_amount"] = new() { Name = "Luma Amount", Type = FilterParameterType.Double, DefaultValue = 1.0, MinValue = -2.0, MaxValue = 5.0 },
            }
        },
        new FilterDefinition
        {
            Id = "cas", Name = "Contrast Adaptive Sharpen", Category = FilterCategory.Sharpen,
            Description = "Sharpens fine detail without the halos unsharp produces (AMD FidelityFX CAS). "
                          + "The right sharpener after an upscale.",
            Parameters = new()
            {
                ["strength"] = new() { Name = "Strength", Type = FilterParameterType.Double, DefaultValue = 0.4, MinValue = 0.0, MaxValue = 1.0 },
            }
        },
        // Film grain — a fine, temporal layer on the luma. The last step of a film-like finish:
        // a cleaned or upscaled picture without it reads as plastic. Pair with tune=grain.
        new FilterDefinition
        {
            Id = "grain", Name = "Film Grain", Category = FilterCategory.Other,
            Description = "Fine, moving film grain on the brightness channel — what keeps a cleaned or "
                          + "upscaled picture reading as film instead of plastic. Put it last, and encode "
                          + "with tune=grain so the encoder keeps it. 4–8 is subtle; past 12 it shows.",
            Parameters = new()
            {
                ["strength"] = new() { Name = "Strength", Type = FilterParameterType.Int, DefaultValue = 6, MinValue = 0, MaxValue = 30 },
                ["chroma"] = new() { Name = "Grain in colour too", Type = FilterParameterType.Bool, DefaultValue = false },
            }
        },
        // Deblock — DCT-domain artifact removal for low-bitrate MPEG/DivX/XviD sources.
        new FilterDefinition
        {
            Id = "deblock", Name = "Deblock (MPEG artifacts)", Category = FilterCategory.Denoise,
            Description = "Removes DCT block artifacts from low-bitrate MPEG-4/DivX/XviD/DVD sources (FFmpeg spp). "
                          + "Run it before denoise and upscale.",
            Parameters = new()
            {
                ["quality"] = new() { Name = "Quality", Type = FilterParameterType.Int, DefaultValue = 4, MinValue = 0, MaxValue = 6 },
                ["strength"] = new() { Name = "Strength (qp)", Type = FilterParameterType.Int, DefaultValue = 5, MinValue = 1, MaxValue = 63 },
            }
        },
        // Color
        new FilterDefinition
        {
            Id = "eq", Name = "Color Adjust", Category = FilterCategory.ColorCorrection,
            Description = "Adjust brightness, contrast, saturation, and gamma",
            Parameters = new()
            {
                ["brightness"] = new() { Name = "Brightness", Type = FilterParameterType.Double, DefaultValue = 0.0, MinValue = -1.0, MaxValue = 1.0 },
                ["contrast"] = new() { Name = "Contrast", Type = FilterParameterType.Double, DefaultValue = 1.0, MinValue = 0.0, MaxValue = 2.0 },
                ["saturation"] = new() { Name = "Saturation", Type = FilterParameterType.Double, DefaultValue = 1.0, MinValue = 0.0, MaxValue = 3.0 },
                ["gamma"] = new() { Name = "Gamma", Type = FilterParameterType.Double, DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10.0 },
            }
        },
        // Frame rate
        new FilterDefinition
        {
            Id = "fps", Name = "Frame Rate", Category = FilterCategory.FrameRate,
            Description = "Convert to a target frame rate by dropping/duplicating frames",
            Parameters = new()
            {
                ["fps"] = new() { Name = "FPS", Type = FilterParameterType.Double, DefaultValue = 23.976 },
            }
        },
        new FilterDefinition
        {
            Id = "mpdecimate", Name = "Decimate Duplicate Frames", Category = FilterCategory.FrameRate,
            Description = "Drop near-duplicate frames by pixel difference — e.g. a 60p stream padded with "
                          + "repeated frames. Output is variable frame rate with the original timing kept, so "
                          + "audio stays in sync. A generic threshold can't follow a 3:2 film cadence reliably; "
                          + "for bobbed telecine use Inverse Telecine instead. Not compatible with chunked encoding.",
            Parameters = new()
            {
                ["hi"] = new() { Name = "Hi threshold", Type = FilterParameterType.Int, DefaultValue = 768, MinValue = 0, MaxValue = 65535 },
                ["lo"] = new() { Name = "Lo threshold", Type = FilterParameterType.Int, DefaultValue = 320, MinValue = 0, MaxValue = 65535 },
                ["frac"] = new() { Name = "Fraction", Type = FilterParameterType.Double, DefaultValue = 0.33, MinValue = 0.0, MaxValue = 1.0 },
            }
        },
        new FilterDefinition
        {
            Id = "grayscale", Name = "Grayscale (strip chroma)", Category = FilterCategory.ColorCorrection,
            Description = "Remove all colour. For black-and-white sources this deletes broadcast chroma noise "
                          + "outright — a cleaner picture and a smaller file — while keeping the 4:2:0 layout "
                          + "every downstream filter and encoder expects.",
            Parameters = new(),
        },
        new FilterDefinition
        {
            Id = "ivtc", Name = "Inverse Telecine (film cadence)", Category = FilterCategory.Deinterlace,
            Description = "Recover the original ~24 fps film frames from telecined video. Handles native "
                          + "interlaced telecine (29.97i) and — with 'Bobbed 60p input' on — a broadcast that was "
                          + "bob-deinterlaced to 60p, by weaving frame pairs back to 30i first. Unlike a "
                          + "deinterlacer it reunites the real fields, so nothing is interpolated: judder-free, "
                          + "and 2.5x fewer frames to encode. Not compatible with chunked encoding.",
            Parameters = new()
            {
                ["bobbed"] = new() { Name = "Bobbed 60p input", Type = FilterParameterType.Bool, DefaultValue = true },
                ["order"] = new()
                {
                    Name = "Field order", Type = FilterParameterType.Choice, DefaultValue = "tff",
                    Options = ["tff", "bff", "auto"]
                },
            }
        },
        new FilterDefinition
        {
            Id = "minterpolate", Name = "Motion Interpolate (frame rate)", Category = FilterCategory.FrameRate,
            Description = "Change frame rate with motion-compensated interpolation — smooth 24->60 or slow-mo, "
                          + "no dropped/duplicated frames. CPU-heavy. (For AI interpolation use --fps-mode rife.)",
            Parameters = new()
            {
                ["fps"] = new() { Name = "Target FPS", Type = FilterParameterType.Double, DefaultValue = 60.0, MinValue = 1, MaxValue = 480 },
                ["mode"] = new()
                {
                    Name = "Mode", Type = FilterParameterType.Choice, DefaultValue = "mci",
                    Options = ["dup", "blend", "mci"]
                },
            }
        },
        // Deband
        new FilterDefinition
        {
            Id = "deband", Name = "Deband", Category = FilterCategory.Denoise,
            Description = "Remove banding artifacts from gradients",
            Parameters = new()
            {
                ["range"] = new() { Name = "Range", Type = FilterParameterType.Int, DefaultValue = 16, MinValue = 0, MaxValue = 64 },
                ["blur"] = new() { Name = "Blur", Type = FilterParameterType.Bool, DefaultValue = true },
            }
        },
        // Tone mapping
        new FilterDefinition
        {
            Id = "tonemap", Name = "Tone Map (HDR→SDR)", Category = FilterCategory.ColorCorrection,
            Description = "Convert HDR to SDR with tone mapping",
            Parameters = new()
            {
                ["tonemap"] = new()
                {
                    Name = "Algorithm", Type = FilterParameterType.Choice, DefaultValue = "hable",
                    Options = ["none", "linear", "gamma", "clip", "reinhard", "hable", "mobius"]
                },
                ["desat"] = new() { Name = "Desaturation", Type = FilterParameterType.Double, DefaultValue = 2.0, MinValue = 0.0, MaxValue = 10.0 },
            }
        },
        // Video stabilization
        new FilterDefinition
        {
            Id = "vidstabtransform", Name = "Video Stabilization", Category = FilterCategory.Other,
            Description = "Stabilize shaky video (requires vidstabdetect first pass)",
            Parameters = new()
            {
                ["smoothing"] = new() { Name = "Smoothing", Type = FilterParameterType.Int, DefaultValue = 15, MinValue = 0, MaxValue = 100 },
                ["zoom"] = new() { Name = "Zoom (%)", Type = FilterParameterType.Int, DefaultValue = 0, MinValue = -50, MaxValue = 50 },
            }
        },
        // Limiter (broadcast safe)
        new FilterDefinition
        {
            Id = "limiter", Name = "Limiter", Category = FilterCategory.ColorCorrection,
            Description = "Limit pixel values to a range (broadcast safe levels)",
            Parameters = new()
            {
                ["min"] = new() { Name = "Min", Type = FilterParameterType.Int, DefaultValue = 16, MinValue = 0, MaxValue = 255 },
                ["max"] = new() { Name = "Max", Type = FilterParameterType.Int, DefaultValue = 235, MinValue = 0, MaxValue = 255 },
            }
        },
        // Crop detect
        new FilterDefinition
        {
            Id = "cropdetect", Name = "Crop Detect", Category = FilterCategory.Crop,
            Description = "Auto-detect and remove black borders",
            Parameters = new()
            {
                ["limit"] = new() { Name = "Limit", Type = FilterParameterType.Int, DefaultValue = 24, MinValue = 0, MaxValue = 255 },
                ["round"] = new() { Name = "Round", Type = FilterParameterType.Int, DefaultValue = 16, MinValue = 0, MaxValue = 16 },
            }
        },
        // SetPTS (speed change)
        new FilterDefinition
        {
            Id = "setpts", Name = "Set PTS (Speed)", Category = FilterCategory.FrameRate,
            Description = "Change video speed by modifying presentation timestamps",
            Parameters = new()
            {
                ["expr"] = new() { Name = "Expression", Type = FilterParameterType.String, DefaultValue = "PTS",
                    Description = "PTS expression: 0.5*PTS = 2x speed, 2.0*PTS = 0.5x speed" },
            }
        },
    ];

    /// <summary>Filter IDs that were skipped during the last GenerateScript call (not recognized by this engine).</summary>
    public IReadOnlyList<string> SkippedFilters => _skippedFilters;
    private List<string> _skippedFilters = [];

    /// <summary>
    /// Decides whether the GPU superres path (libplacebo + Vulkan) is usable. Wired by AppServices
    /// to a real capability probe; defaults to available so unit tests are deterministic. When it
    /// returns false, the <c>superres</c> filter degrades to a plain spline upscale.
    /// </summary>
    public Func<bool> IsSuperResAvailable { get; set; } = static () => true;

    public string GenerateScript(FilterChain chain, string inputFile)
    {
        _skippedFilters = [];

        if (chain.Filters.Count == 0)
            return string.Empty;

        var parts = new List<string>();

        foreach (var applied in chain.Filters)
        {
            var filterStr = applied.Filter.Id switch
            {
                "crop" => BuildCropFilter(applied),
                "resize" => BuildResizeFilter(applied),
                "superres" => BuildSuperResFilter(applied),
                "yadif" => BuildYadifFilter(applied),
                "bwdif" => BuildBwdifFilter(applied),
                "nlmeans" => BuildNlmeansFilter(applied),
                "hqdn3d" => BuildHqdn3dFilter(applied),
                "unsharp" => BuildUnsharpFilter(applied),
                "cas" => BuildCasFilter(applied),
                "grain" => BuildGrainFilter(applied),
                "deblock" => BuildDeblockFilter(applied),
                "eq" => BuildEqFilter(applied),
                "fps" => BuildFpsFilter(applied),
                "mpdecimate" => BuildMpdecimateFilter(applied),
                "grayscale" => BuildGrayscaleFilter(applied),
                "ivtc" => BuildIvtcFilter(applied),
                "minterpolate" => BuildMinterpolateFilter(applied),
                "deband" => BuildDebandFilter(applied),
                "tonemap" => BuildTonemapFilter(applied),
                "vidstabtransform" => BuildVidstabFilter(applied),
                "limiter" => BuildLimiterFilter(applied),
                "cropdetect" => BuildCropdetectFilter(applied),
                "setpts" => BuildSetptsFilter(applied),
                _ => null
            };

            if (filterStr is not null)
                parts.Add(filterStr);
            else
                _skippedFilters.Add(applied.Filter.Id);
        }

        return string.Join(",", parts);
    }

    public Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    private static string BuildCropFilter(AppliedFilter af)
    {
        var l = GetInt(af, "left");
        var t = GetInt(af, "top");
        var r = GetInt(af, "right");
        var b = GetInt(af, "bottom");
        return $"crop=iw-{l + r}:ih-{t + b}:{l}:{t}";
    }

    private static string? BuildResizeFilter(AppliedFilter af)
    {
        var w = GetInt(af, "width", -1);
        var h = GetInt(af, "height", -1);
        var algo = GetString(af, "algorithm", "lanczos");
        // FFmpeg can derive one dimension from the other via a negative value, but not both:
        // "scale=-2:-2" / "scale=-1:-1" fails filtergraph configuration ("Cannot scale to ...").
        // A resize with no usable target dimension is a no-op, so treat it as pass-through
        // (skipped) rather than emitting an invalid graph that aborts the whole encode.
        if (w <= 0 && h <= 0)
            return null;
        return $"scale={w}:{h}:flags={algo}";
    }

    private string BuildSuperResFilter(AppliedFilter af)
    {
        var model = GetString(af, "model", "fsrcnnx");
        var height = GetInt(af, "height", 1080);
        var shader = GpuShaders.FileNameFor(model);

        // No Vulkan/libplacebo, or an unrecognized model: still upscale, just with a classical
        // scaler so the AI profile keeps producing HD output on machines without a usable GPU.
        if (shader is null || !IsSuperResAvailable())
            return $"scale=-2:{height}:flags=spline";

        // libplacebo runs the neural shader AND the scale on the Vulkan device; hwupload/hwdownload
        // move the frame on and off the GPU. w=-2 keeps the source aspect at the target height.
        // The bare shader filename resolves against FFmpeg's working directory, which the video
        // processor sets to GpuShaders.Directory to avoid filter-graph path escaping.
        return $"format=yuv420p,hwupload,libplacebo=w=-2:h={height}:upscaler=ewa_lanczos:custom_shader_path={shader},hwdownload,format=yuv420p";
    }

    private static string BuildYadifFilter(AppliedFilter af)
    {
        var mode = GetString(af, "mode", "send_frame");
        var parity = GetString(af, "parity", "auto");
        return $"yadif=mode={mode}:parity={parity}";
    }

    private static string BuildBwdifFilter(AppliedFilter af)
    {
        var mode = GetString(af, "mode", "send_frame");
        return $"bwdif=mode={mode}";
    }

    private static string BuildNlmeansFilter(AppliedFilter af)
    {
        var s = GetDouble(af, "strength", 3.0);
        var p = GetInt(af, "patch_size", 7);
        var r = GetInt(af, "search_size", 15);
        return $"nlmeans=s={Fmt(s)}:p={p}:r={r}";
    }

    private static string BuildHqdn3dFilter(AppliedFilter af)
    {
        var ls = GetDouble(af, "luma_spatial", 4.0);
        var cs = GetDouble(af, "chroma_spatial", 3.0);
        var lt = GetDouble(af, "luma_temporal", 6.0);
        return $"hqdn3d={Fmt(ls)}:{Fmt(cs)}:{Fmt(lt)}";
    }

    private static string BuildUnsharpFilter(AppliedFilter af)
    {
        var lx = GetInt(af, "luma_x", 5);
        var ly = GetInt(af, "luma_y", 5);
        var la = GetDouble(af, "luma_amount", 1.0);
        return $"unsharp={lx}:{ly}:{Fmt(la)}";
    }

    /// <summary>
    /// FFmpeg's <c>noise</c> with temporal (<c>t</c>) averaged (<c>a</c>) noise: a new, smooth-edged
    /// pattern every frame, like grain, rather than a frozen mesh or harsh salt-and-pepper. Luma only
    /// by default — colour noise reads as video, not film — with a half-strength chroma option.
    /// </summary>
    public static string BuildGrainFilter(AppliedFilter af)
    {
        var s = Math.Clamp(GetInt(af, "strength", 6), 0, 30);
        if (s == 0) return "null";
        var chroma = GetBool(af, "chroma", false);
        var c = Math.Max(1, s / 2);
        return chroma
            ? $"noise=c0s={s}:c0f=t+a:c1s={c}:c1f=t+a:c2s={c}:c2f=t+a"
            : $"noise=c0s={s}:c0f=t+a";
    }

    private static string BuildCasFilter(AppliedFilter af)
    {
        var s = GetDouble(af, "strength", 0.4);
        return $"cas=strength={Fmt(s)}";
    }

    private static string BuildDeblockFilter(AppliedFilter af)
    {
        var quality = GetInt(af, "quality", 4);
        var qp = GetInt(af, "strength", 5);
        return $"spp=quality={quality}:qp={qp}";
    }

    private static string BuildEqFilter(AppliedFilter af)
    {
        var br = GetDouble(af, "brightness", 0.0);
        var co = GetDouble(af, "contrast", 1.0);
        var sa = GetDouble(af, "saturation", 1.0);
        var ga = GetDouble(af, "gamma", 1.0);
        return $"eq=brightness={Fmt(br)}:contrast={Fmt(co)}:saturation={Fmt(sa)}:gamma={Fmt(ga)}";
    }

    private static string BuildFpsFilter(AppliedFilter af)
    {
        var fps = GetDouble(af, "fps", 23.976);
        return $"fps={Fmt(fps)}";
    }

    private static string BuildMpdecimateFilter(AppliedFilter af)
    {
        var hi = GetInt(af, "hi", 768);
        var lo = GetInt(af, "lo", 320);
        var frac = GetDouble(af, "frac", 0.33);
        return $"mpdecimate=hi={hi}:lo={lo}:frac={Fmt(frac)}";
    }

    // hue=s=0 rather than format=gray: it zeroes the chroma planes but keeps yuv420p, so every
    // downstream filter and encoder still sees the pixel format it expects.
    private static string BuildGrayscaleFilter(AppliedFilter af) => "hue=s=0";

    private static string BuildIvtcFilter(AppliedFilter af)
    {
        var order = GetString(af, "order", "tff");
        // fps=source_fps first: decimate rewrites timestamps assuming a constant input rate, so a
        // reception dropout in a broadcast capture (a pts gap with no frames) silently vanishes from
        // the video while the separately encoded audio keeps the gap - the picture then runs ahead
        // of the sound by the cumulative gap (1.8 s by minute 55 on one Gunsmoke capture). Filling
        // the gap with duplicate frames keeps the timeline: a dropout becomes a short freeze with
        // silent audio, in sync, instead of a growing lead.
        var ivtc = $"fps=source_fps,fieldmatch=order={order}:combmatch=full,decimate";
        if (!GetBool(af, "bobbed", true)) return ivtc;

        // A bob-deinterlaced 60p stream still carries the 3:2 field structure; weaving each pair
        // of consecutive frames back into ONE same-height 30i frame lets fieldmatch see it.
        // interleave_top/bottom take alternate lines from the two frames — NOT mode=merge, which
        // stacks them into a double-height frame and silently halves the picture's width once a
        // later height-based resize runs. Verified on MeTV 720p60 captures: idet reads the woven
        // stream as pure TFF/BFF, the frame stays 1104x720, and decimate lands on 23.98 fps where a
        // pixel-difference decimator (mpdecimate) could not find a threshold that held across episodes.
        var weave = order == "bff" ? "interleave_bottom" : "interleave_top";
        return $"fps=source_fps,tinterlace=mode={weave},{ivtc.Replace("fps=source_fps,", "")}";
    }

    // Values arrive as bool from the UI, JsonElement from a saved profile/preset, or a string
    // from the CLI — accept all three rather than trusting one shape.
    private static bool GetBool(AppliedFilter af, string key, bool def)
    {
        if (!af.ParameterValues.TryGetValue(key, out var v) || v is null) return def;
        return v switch
        {
            bool b => b,
            System.Text.Json.JsonElement e => e.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => def,
            },
            string s => bool.TryParse(s, out var parsed) ? parsed : def,
            _ => def,
        };
    }

    private static string BuildMinterpolateFilter(AppliedFilter af)
    {
        var fps = GetDouble(af, "fps", 60.0);
        var mode = GetString(af, "mode", "mci");
        // Full motion-compensated interpolation is the smooth one; dup/blend are cheaper fallbacks.
        return mode == "mci"
            ? $"minterpolate=fps={Fmt(fps)}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1"
            : $"minterpolate=fps={Fmt(fps)}:mi_mode={mode}";
    }

    private static string BuildDebandFilter(AppliedFilter af)
    {
        var range = GetInt(af, "range", 16);
        var blur = !af.ParameterValues.TryGetValue("blur", out var v) || Convert.ToBoolean(Unwrap(v)); // default true
        return $"deband=range={range}:blur={(blur ? 1 : 0)}";
    }

    private static string BuildTonemapFilter(AppliedFilter af)
    {
        var algo = GetString(af, "tonemap", "hable");
        var desat = GetDouble(af, "desat", 2.0);
        return $"tonemap={algo}:desat={Fmt(desat)}";
    }

    private static string BuildVidstabFilter(AppliedFilter af)
    {
        var smoothing = GetInt(af, "smoothing", 15);
        var zoom = GetInt(af, "zoom", 0);
        return $"vidstabtransform=smoothing={smoothing}:zoom={zoom}";
    }

    private static string BuildLimiterFilter(AppliedFilter af)
    {
        var min = GetInt(af, "min", 16);
        var max = GetInt(af, "max", 235);
        return $"limiter=min={min}:max={max}";
    }

    private static string BuildCropdetectFilter(AppliedFilter af)
    {
        var limit = GetInt(af, "limit", 24);
        var round = GetInt(af, "round", 16);
        return $"cropdetect=limit={limit}:round={round}";
    }

    private static string BuildSetptsFilter(AppliedFilter af)
    {
        var expr = GetString(af, "expr", "PTS");
        return $"setpts={expr}";
    }

    // Filters carried on a profile are serialized to JSON, so their parameter values come back
    // as JsonElement rather than int/double/bool/string. Convert.ToXxx can't read a JsonElement,
    // so unwrap it to the underlying primitive first — otherwise every profile-carried filter
    // throws the moment its script is generated.
    private static object? Unwrap(object? v)
    {
        if (v is System.Text.Json.JsonElement je)
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.String => je.GetString(),
                _ => je.ToString()
            };
        return v;
    }

    private static int GetInt(AppliedFilter af, string key, int def = 0)
        => af.ParameterValues.TryGetValue(key, out var v) && Unwrap(v) is { } u ? Convert.ToInt32(u, CultureInfo.InvariantCulture) : def;

    private static double GetDouble(AppliedFilter af, string key, double def = 0)
        => af.ParameterValues.TryGetValue(key, out var v) && Unwrap(v) is { } u ? Convert.ToDouble(u, CultureInfo.InvariantCulture) : def;

    private static string GetString(AppliedFilter af, string key, string def = "")
        => af.ParameterValues.TryGetValue(key, out var v) ? Unwrap(v)?.ToString() ?? def : def;

    private static string Fmt(double v) => v.ToString("G", CultureInfo.InvariantCulture);
}
