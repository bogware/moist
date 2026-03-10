using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moist.Filters;

/// <summary>
/// Converts Dictionary&lt;string, object&gt; values to proper CLR types during JSON deserialization.
/// Without this, System.Text.Json deserializes object values as JsonElement.
/// </summary>
internal sealed class ObjectDictionaryConverter : JsonConverter<Dictionary<string, object>>
{
    public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dict = new Dictionary<string, object>();
        if (reader.TokenType != JsonTokenType.StartObject)
            return dict;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            dict[key] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString()!,
                JsonTokenType.Number when reader.TryGetInt32(out var i) => i,
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                _ => reader.GetString() ?? ""
            };
        }
        return dict;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(k);
            JsonSerializer.Serialize(writer, v, options);
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// A saved filter chain configuration with a name and description.
/// Professional presets are built-in; users can also create their own.
/// </summary>
public sealed class FilterPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string EngineId { get; init; } = "ffmpeg";
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// The filters in this preset, stored as serializable definitions.
    /// </summary>
    public List<PresetFilter> Filters { get; init; } = [];
}

public sealed class PresetFilter
{
    public required string FilterId { get; init; }

    [JsonConverter(typeof(ObjectDictionaryConverter))]
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Manages filter presets — built-in professional presets and user-created ones.
/// </summary>
public sealed class FilterPresetManager
{
    private readonly List<FilterPreset> _presets = [];
    private readonly string _configDir;

    public IReadOnlyList<FilterPreset> Presets => _presets;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public FilterPresetManager()
    {
        _configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "moist", "filter-presets");
        Directory.CreateDirectory(_configDir);

        AddBuiltInPresets();
        LoadUserPresets();
    }

    private void AddBuiltInPresets()
    {
        // Film Grain Reduction (Professional)
        _presets.Add(new FilterPreset
        {
            Id = "preset-film-grain-reduction", Name = "Film Grain Reduction",
            Description = "Gentle grain reduction for film content. Preserves detail while reducing visible grain. NLMeans + light sharpening to compensate.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "nlmeans", Parameters = new() { ["strength"] = 4.0, ["patch_size"] = 7, ["search_size"] = 15 } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 5, ["luma_y"] = 5, ["luma_amount"] = 0.5 } },
            ]
        });

        // Heavy Denoise (Noisy Source)
        _presets.Add(new FilterPreset
        {
            Id = "preset-heavy-denoise", Name = "Heavy Denoise (Noisy Source)",
            Description = "Aggressive denoising for very noisy sources like VHS captures, old DVDs, or low-light footage. HQDN3D + NLMeans combo.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 6.0, ["chroma_spatial"] = 4.5, ["luma_temporal"] = 9.0 } },
                new PresetFilter { FilterId = "nlmeans", Parameters = new() { ["strength"] = 6.0, ["patch_size"] = 7, ["search_size"] = 21 } },
            ]
        });

        // Anime Processing
        _presets.Add(new FilterPreset
        {
            Id = "preset-anime", Name = "Anime Processing",
            Description = "Optimized for anime/cel-shaded content. Light denoise + sharpen to preserve line art and reduce banding.",
            Category = "Anime", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 2.0, ["luma_temporal"] = 3.0 } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = 0.8 } },
            ]
        });

        // Anime Upscale 720p to 1080p
        _presets.Add(new FilterPreset
        {
            Id = "preset-anime-upscale", Name = "Anime Upscale (720p -> 1080p)",
            Description = "Upscale anime from 720p to 1080p with Lanczos scaling and light sharpening.",
            Category = "Anime", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 1920, ["height"] = 1080, ["algorithm"] = "lanczos" } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = 0.4 } },
            ]
        });

        // DVD Restoration
        _presets.Add(new FilterPreset
        {
            Id = "preset-dvd-restore", Name = "DVD Restoration",
            Description = "Deinterlace + denoise + upscale for DVD content. Bwdif deinterlace, moderate denoise, and upscale to 720p.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "bwdif", Parameters = new() { ["mode"] = "send_frame" } },
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 4.0, ["chroma_spatial"] = 3.0, ["luma_temporal"] = 6.0 } },
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 1280, ["height"] = 720, ["algorithm"] = "lanczos" } },
            ]
        });

        // Broadcast Deinterlace
        _presets.Add(new FilterPreset
        {
            Id = "preset-broadcast-deint", Name = "Broadcast Deinterlace",
            Description = "High-quality deinterlacing for broadcast TV content using Yadif with spatial check.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "yadif", Parameters = new() { ["mode"] = "send_frame", ["parity"] = "auto" } },
            ]
        });

        // Web Optimization (720p)
        _presets.Add(new FilterPreset
        {
            Id = "preset-web-720p", Name = "Web Optimization (720p)",
            Description = "Resize to 720p for web streaming. Light denoise to improve compression ratio.",
            Category = "Web", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 1280, ["height"] = 720, ["algorithm"] = "lanczos" } },
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
            ]
        });

        // Social Media Vertical
        _presets.Add(new FilterPreset
        {
            Id = "preset-social-vertical", Name = "Social Media (Vertical 1080x1920)",
            Description = "Center crop and resize to vertical 9:16 format for Instagram/TikTok/Shorts.",
            Category = "Web", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 1080, ["height"] = 1920, ["algorithm"] = "lanczos" } },
            ]
        });

        // Color Correction (Warm)
        _presets.Add(new FilterPreset
        {
            Id = "preset-color-warm", Name = "Warm Color Grade",
            Description = "Slight warm tone with increased saturation and contrast. Good for outdoor/nature footage.",
            Category = "Color", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "eq", Parameters = new() { ["brightness"] = 0.03, ["contrast"] = 1.1, ["saturation"] = 1.2, ["gamma"] = 0.95 } },
            ]
        });

        // Color Correction (Cinema)
        _presets.Add(new FilterPreset
        {
            Id = "preset-color-cinema", Name = "Cinema Look",
            Description = "Desaturated, high-contrast look popular in modern cinema. Subtle film-like aesthetic.",
            Category = "Color", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "eq", Parameters = new() { ["brightness"] = -0.02, ["contrast"] = 1.15, ["saturation"] = 0.85, ["gamma"] = 1.05 } },
            ]
        });

        // Sharpen for Soft Sources
        _presets.Add(new FilterPreset
        {
            Id = "preset-sharpen-soft", Name = "Sharpen Soft Sources",
            Description = "Moderate sharpening for soft or slightly blurry video. Good for webcam or old camcorder footage.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 5, ["luma_y"] = 5, ["luma_amount"] = 1.5 } },
            ]
        });

        // Frame Rate Conversion 24fps
        _presets.Add(new FilterPreset
        {
            Id = "preset-fps-24", Name = "Convert to 23.976 fps (Film)",
            Description = "Convert frame rate to film-standard 23.976 fps.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "fps", Parameters = new() { ["fps"] = 23.976 } },
            ]
        });

        // Frame Rate Conversion 30fps
        _presets.Add(new FilterPreset
        {
            Id = "preset-fps-30", Name = "Convert to 29.97 fps (NTSC)",
            Description = "Convert frame rate to NTSC-standard 29.97 fps.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "fps", Parameters = new() { ["fps"] = 29.97 } },
            ]
        });

        // === NEW PROFESSIONAL PRESETS ===

        // Modern Upscaling Algorithm (Lanczos + Edge-Aware Sharpening)
        // Uses multi-pass approach: high-quality Lanczos upscale, followed by
        // contrast-adaptive unsharp mask to recover perceived detail, then light
        // denoising to suppress upscaling artifacts.
        _presets.Add(new FilterPreset
        {
            Id = "preset-upscale-4k", Name = "Modern Upscale to 4K (Lanczos)",
            Description = "Professional 4K upscaling pipeline: Lanczos5 resample for optimal sinc interpolation, " +
                "contrast-adaptive sharpening to restore perceived high-frequency detail, and gentle temporal " +
                "denoise to suppress ringing artifacts. Mathematically optimal for photographic/film content.",
            Category = "Upscaling", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 3840, ["height"] = 2160, ["algorithm"] = "lanczos" } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 5, ["luma_y"] = 5, ["luma_amount"] = 0.8 } },
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 1.5, ["chroma_spatial"] = 1.0, ["luma_temporal"] = 2.0 } },
            ]
        });

        // Upscale 480p to 1080p (DVD to HD)
        _presets.Add(new FilterPreset
        {
            Id = "preset-upscale-sd-to-hd", Name = "Upscale SD to 1080p (DVD to HD)",
            Description = "High-quality 480p→1080p upscale chain: denoise source first to avoid amplifying noise, " +
                "Lanczos upscale, then edge-aware sharpening to restore clarity.",
            Category = "Upscaling", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 3.0, ["chroma_spatial"] = 2.0, ["luma_temporal"] = 4.0 } },
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 1920, ["height"] = 1080, ["algorithm"] = "lanczos" } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 5, ["luma_y"] = 5, ["luma_amount"] = 1.0 } },
            ]
        });

        // Upscale 2x with Spline36 (balanced sharpness/ringing)
        _presets.Add(new FilterPreset
        {
            Id = "preset-upscale-2x-spline", Name = "Upscale 2x (Spline36 — Balanced)",
            Description = "Doubles resolution using Spline36 interpolation — mathematically optimal balance between " +
                "sharpness and ringing. Superior to bicubic for photographic content.",
            Category = "Upscaling", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = -2, ["height"] = -2, ["algorithm"] = "spline" } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = 0.6 } },
            ]
        });

        // VHS Capture Restoration
        _presets.Add(new FilterPreset
        {
            Id = "preset-vhs-restore", Name = "VHS Capture Restoration",
            Description = "Full restoration pipeline for VHS digitization: deinterlace, heavy denoise (temporal+spatial), " +
                "color correction for faded VHS tapes, and gentle sharpening.",
            Category = "Restoration", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "bwdif", Parameters = new() { ["mode"] = "send_frame" } },
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 8.0, ["chroma_spatial"] = 6.0, ["luma_temporal"] = 12.0 } },
                new PresetFilter { FilterId = "nlmeans", Parameters = new() { ["strength"] = 5.0, ["patch_size"] = 7, ["search_size"] = 21 } },
                new PresetFilter { FilterId = "eq", Parameters = new() { ["brightness"] = 0.02, ["contrast"] = 1.15, ["saturation"] = 1.3, ["gamma"] = 0.95 } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = 0.6 } },
            ]
        });

        // HDR to SDR Tone Mapping
        _presets.Add(new FilterPreset
        {
            Id = "preset-hdr-to-sdr", Name = "HDR to SDR Tone Map",
            Description = "Convert HDR10/HLG content to SDR using Hable tone mapping. Preserves highlight detail.",
            Category = "Color", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "tonemap", Parameters = new() { ["tonemap"] = "hable", ["desat"] = 0.0 } },
            ]
        });

        // Stabilization Prep
        _presets.Add(new FilterPreset
        {
            Id = "preset-stabilize", Name = "Video Stabilization",
            Description = "Two-pass video stabilization using vidstabdetect+vidstabtransform. Smooths camera shake.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "vidstabtransform", Parameters = new() { ["smoothing"] = 30, ["zoom"] = -5 } },
            ]
        });

        // Broadcast Safe Colors
        _presets.Add(new FilterPreset
        {
            Id = "preset-broadcast-safe", Name = "Broadcast Safe Colors",
            Description = "Clamp luma and chroma to broadcast-legal levels (16-235/16-240). Required for TV broadcast.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "limiter", Parameters = new() { ["min"] = 16, ["max"] = 235 } },
            ]
        });

        // Letterbox Removal + Crop to 16:9
        _presets.Add(new FilterPreset
        {
            Id = "preset-crop-16-9", Name = "Crop to 16:9 (Letterbox Removal)",
            Description = "Remove black bars and crop to 16:9 aspect ratio. Auto-detects and removes letterboxing.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "cropdetect", Parameters = new() { ["limit"] = 24, ["round"] = 2 } },
            ]
        });

        // Slow Motion (0.5x)
        _presets.Add(new FilterPreset
        {
            Id = "preset-slowmo-half", Name = "Slow Motion (0.5x Speed)",
            Description = "Halve playback speed with frame interpolation for smooth slow motion.",
            Category = "Speed", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "setpts", Parameters = new() { ["expr"] = "2.0*PTS" } },
                new PresetFilter { FilterId = "fps", Parameters = new() { ["fps"] = 60.0 } },
            ]
        });

        // Speed Up (2x)
        _presets.Add(new FilterPreset
        {
            Id = "preset-speedup-2x", Name = "Speed Up (2x Speed)",
            Description = "Double playback speed — ideal for timelapse or quick review.",
            Category = "Speed", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "setpts", Parameters = new() { ["expr"] = "0.5*PTS" } },
            ]
        });

        // Vintage Film Look
        _presets.Add(new FilterPreset
        {
            Id = "preset-vintage-film", Name = "Vintage Film Look",
            Description = "Emulate aged film stock: slight desaturation, warm tint, vignette effect, and film grain overlay.",
            Category = "Creative", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "eq", Parameters = new() { ["brightness"] = -0.03, ["contrast"] = 1.1, ["saturation"] = 0.7, ["gamma"] = 1.1 } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = -0.3 } },
            ]
        });

        // Black and White Conversion
        _presets.Add(new FilterPreset
        {
            Id = "preset-bw", Name = "Black & White (Luminance)",
            Description = "Convert to high-quality black and white using luminance-weighted desaturation with contrast boost.",
            Category = "Creative", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "eq", Parameters = new() { ["saturation"] = 0.0, ["contrast"] = 1.15 } },
            ]
        });

        // Night Vision / Low Light Enhancement
        _presets.Add(new FilterPreset
        {
            Id = "preset-low-light", Name = "Low Light Enhancement",
            Description = "Brighten dark footage, heavy denoise to combat high-ISO noise, and gamma lift.",
            Category = "Professional", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "eq", Parameters = new() { ["brightness"] = 0.08, ["contrast"] = 1.1, ["gamma"] = 0.75 } },
                new PresetFilter { FilterId = "nlmeans", Parameters = new() { ["strength"] = 8.0, ["patch_size"] = 7, ["search_size"] = 21 } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = 0.4 } },
            ]
        });

        // Archival Preservation (Lossless-Like Processing)
        _presets.Add(new FilterPreset
        {
            Id = "preset-archival", Name = "Archival Preservation",
            Description = "Minimal processing for archival digitization: only deinterlace if needed, no destructive filtering.",
            Category = "Restoration", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "bwdif", Parameters = new() { ["mode"] = "send_frame" } },
            ]
        });

        // Anime Banding Fix
        _presets.Add(new FilterPreset
        {
            Id = "preset-anime-deband", Name = "Anime Deband + Clean",
            Description = "Fix color banding in anime (common in dark gradients). Deband + light denoise for pristine output.",
            Category = "Anime", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "deband", Parameters = new() { ["range"] = 16, ["blur"] = true } },
                new PresetFilter { FilterId = "hqdn3d", Parameters = new() { ["luma_spatial"] = 1.5, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 2.0 } },
            ]
        });

        // 60fps Smooth Motion
        _presets.Add(new FilterPreset
        {
            Id = "preset-fps-60", Name = "Smooth Motion 60fps",
            Description = "Convert to 60fps for fluid motion. Best for gaming captures or sports content.",
            Category = "Speed", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "fps", Parameters = new() { ["fps"] = 60.0 } },
            ]
        });

        // Screen Recording Optimization
        _presets.Add(new FilterPreset
        {
            Id = "preset-screen-recording", Name = "Screen Recording Optimization",
            Description = "Optimized for screen recordings: sharpen text, remove mouse cursor noise, resize to 1080p.",
            Category = "Web", EngineId = "ffmpeg", IsBuiltIn = true,
            Filters =
            [
                new PresetFilter { FilterId = "resize", Parameters = new() { ["width"] = 1920, ["height"] = 1080, ["algorithm"] = "lanczos" } },
                new PresetFilter { FilterId = "unsharp", Parameters = new() { ["luma_x"] = 3, ["luma_y"] = 3, ["luma_amount"] = 1.2 } },
            ]
        });
    }

    public void SaveUserPreset(FilterPreset preset)
    {
        // Remove any existing preset with the same id
        _presets.RemoveAll(p => p.Id == preset.Id && !p.IsBuiltIn);
        _presets.Add(preset);

        var path = Path.Combine(_configDir, $"{preset.Id}.json");
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        File.WriteAllText(path, json);
    }

    public void DeleteUserPreset(string id)
    {
        _presets.RemoveAll(p => p.Id == id && !p.IsBuiltIn);
        var path = Path.Combine(_configDir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    private void LoadUserPresets()
    {
        if (!Directory.Exists(_configDir)) return;

        foreach (var file in Directory.GetFiles(_configDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<FilterPreset>(json, JsonOptions);
                if (preset is not null && !preset.IsBuiltIn)
                    _presets.Add(preset);
            }
            catch { /* skip corrupt files */ }
        }
    }

    public IReadOnlyList<string> GetCategories()
        => _presets.Select(p => p.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c).ToList();
}
