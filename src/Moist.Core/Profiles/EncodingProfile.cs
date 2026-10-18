using System.Text.Json;
using System.Text.Json.Serialization;
using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Muxers;

namespace Moist.Core.Profiles;

/// <summary>
/// A saved encoding profile containing all settings for a complete encode job.
/// Users can save and load profiles for common workflows.
/// </summary>
public sealed class EncodingProfile
{
    public string Name { get; set; } = "Untitled";
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    // Video settings
    public string VideoEncoderId { get; set; } = "ffmpeg-x265";
    public VideoEncodingMode VideoMode { get; set; } = VideoEncodingMode.ConstantRateFactor;
    public double VideoQuality { get; set; } = 23.0;
    public int VideoBitrate { get; set; } = 5000;
    public VideoPreset VideoPreset { get; set; } = VideoPreset.Medium;
    public VideoTune VideoTune { get; set; } = VideoTune.None;
    public int SvtAv1Preset { get; set; } = 6;
    public string VideoCustomArgs { get; set; } = string.Empty;

    /// <summary>Output bit depth. <see cref="VideoBitDepth.Auto"/> follows the source.</summary>
    public VideoBitDepth BitDepth { get; set; } = VideoBitDepth.Auto;

    /// <summary>Real-ESRGAN upscale model ("x4plus"/"animevideov3"), or null for none. When set, the
    /// profile's <see cref="Filters"/> are the pre-clean and the processor does the upscale + finish.</summary>
    public string? AiUpscaleModel { get; set; }
    public int AiUpscaleHeight { get; set; } = 1080;

    // Audio settings
    public string AudioEncoderId { get; set; } = "ffmpeg-audio-opus";
    public AudioBitrateMode AudioBitrateMode { get; set; } = AudioBitrateMode.Vbr;
    public int AudioBitrate { get; set; } = 128;
    public string AudioCustomArgs { get; set; } = string.Empty;
    public AudioChannelMode AudioChannelMode { get; set; } = AudioChannelMode.KeepOriginal;
    public int AudioSampleRate { get; set; }
    public AudioNormalizationMode AudioNormalization { get; set; } = AudioNormalizationMode.None;
    public double NormalizationTarget { get; set; } = -16.0;
    public double TruePeakLimit { get; set; } = -1.0;
    public AudioSampleFormat AudioSampleFormat { get; set; } = AudioSampleFormat.Auto;
    public bool AudioOnly { get; set; }

    // Container
    public ContainerFormat Container { get; set; } = ContainerFormat.Mkv;

    // Filter chain (optional — engine ID + list of filter ID + param pairs)
    public string? FilterEngineId { get; set; }
    public List<ProfileFilter> Filters { get; set; } = [];
}

/// <summary>Serializable filter entry within a profile.</summary>
public sealed class ProfileFilter
{
    public string FilterId { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// Manages saving, loading, and listing encoding profiles.
/// </summary>
public sealed class ProfileManager
{
    private readonly string _profilesDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProfileManager()
    {
        _profilesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "moist", "profiles");
        Directory.CreateDirectory(_profilesDir);

        // Ensure built-in profiles exist
        EnsureBuiltInProfiles();
    }

    public List<EncodingProfile> GetAllProfiles()
    {
        var profiles = new List<EncodingProfile>();
        foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<EncodingProfile>(json, JsonOptions);
                if (profile is not null)
                    profiles.Add(profile);
            }
            catch { /* skip corrupt profiles */ }
        }
        return profiles.OrderBy(p => p.Name).ToList();
    }

    public async Task<List<EncodingProfile>> GetAllProfilesAsync(CancellationToken ct = default)
    {
        var profiles = new List<EncodingProfile>();
        foreach (var file in Directory.GetFiles(_profilesDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var profile = JsonSerializer.Deserialize<EncodingProfile>(json, JsonOptions);
                if (profile is not null)
                    profiles.Add(profile);
            }
            catch { /* skip corrupt profiles */ }
        }
        return profiles.OrderBy(p => p.Name).ToList();
    }

    public void Save(EncodingProfile profile)
    {
        profile.ModifiedAt = DateTime.UtcNow;
        var fileName = SanitizeFileName(profile.Name) + ".json";
        var path = Path.Combine(_profilesDir, fileName);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        // Write to a temp file then move into place so an interrupted write
        // can't truncate an existing profile (mirrors MoistSettings.Save).
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task SaveAsync(EncodingProfile profile, CancellationToken ct = default)
    {
        profile.ModifiedAt = DateTime.UtcNow;
        var fileName = SanitizeFileName(profile.Name) + ".json";
        var path = Path.Combine(_profilesDir, fileName);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        // Write to a temp file then move into place so an interrupted write
        // can't truncate an existing profile (mirrors MoistSettings.Save).
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string profileName)
    {
        var fileName = SanitizeFileName(profileName) + ".json";
        var path = Path.Combine(_profilesDir, fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public EncodingProfile? Load(string profileName)
    {
        var fileName = SanitizeFileName(profileName) + ".json";
        var path = Path.Combine(_profilesDir, fileName);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<EncodingProfile>(json, JsonOptions);
    }

    public async Task<EncodingProfile?> LoadAsync(string profileName, CancellationToken ct = default)
    {
        var fileName = SanitizeFileName(profileName) + ".json";
        var path = Path.Combine(_profilesDir, fileName);
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<EncodingProfile>(json, JsonOptions);
    }

    private void EnsureBuiltInProfiles()
    {
        var builtIns = GetBuiltInProfiles();
        foreach (var profile in builtIns)
        {
            var path = Path.Combine(_profilesDir, SanitizeFileName(profile.Name) + ".json");
            // Re-materialize a built-in when its file is absent OR present but
            // corrupt (a truncated write survives on disk and would otherwise be
            // silently skipped by GetAllProfiles and never re-created).
            if (!File.Exists(path) || !IsReadableProfile(path))
                Save(profile);
        }
    }

    private static bool IsReadableProfile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<EncodingProfile>(json, JsonOptions) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static List<EncodingProfile> GetBuiltInProfiles() =>
    [
        new()
        {
            // One-click restoration of vintage SD DVDRips (DivX/XviD/DVD) to sharp HD.
            // Restore at native res, THEN upscale, THEN sharpen — order matters. The resize
            // uses -2:1080 so it lands at 1080p while preserving the source aspect (a 4:3 512x384
            // tape becomes 1440x1080). x265 10-bit here is portable; swap the encoder to
            // hw-nvenc-hevc for a much faster GPU encode where NVENC is available.
            Name = "Restore SD to HD (vintage)",
            Description = "Old SD tape to sharp 1080p: de-block, denoise, spline upscale, adaptive sharpen, deband. HEVC 10-bit.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 19,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 192,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            Filters =
            [
                new() { FilterId = "deblock", Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",  Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
                new() { FilterId = "resize",  Parameters = new() { ["width"] = -2, ["height"] = 1080, ["algorithm"] = "spline" } },
                new() { FilterId = "cas",     Parameters = new() { ["strength"] = 0.5 } },
                new() { FilterId = "deband",  Parameters = new() { ["range"] = 16 } },
            ],
        },
        new()
        {
            // GPU variant of the restore profile: a neural-shader upscale (FSRCNNX via libplacebo)
            // in place of the spline scale, with a lighter CAS since the shader adds its own edge
            // definition. Needs a Vulkan GPU; the superres filter falls back to spline automatically
            // where one isn't present, so this is safe to ship as a built-in.
            Name = "AI Upscale SD to HD (GPU)",
            Description = "Vintage SD to sharp 1080p with a GPU neural upscale (FSRCNNX via libplacebo). Needs a Vulkan GPU; falls back to spline without one.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 19,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 192,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            Filters =
            [
                new() { FilterId = "deblock",  Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",   Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
                new() { FilterId = "superres", Parameters = new() { ["model"] = "fsrcnnx", ["height"] = 1080 } },
                new() { FilterId = "cas",      Parameters = new() { ["strength"] = 0.4 } },
                new() { FilterId = "deband",   Parameters = new() { ["range"] = 16 } },
            ],
        },
        new()
        {
            // Deep-model variant: Real-ESRGAN (frame-based) instead of the libplacebo shader.
            // Highest quality, but slow and disk-heavy — the processor caps source length unless
            // overridden. The Filters here are ONLY the pre-clean (de-block + denoise); the
            // RealEsrganVideoProcessor does the upscale and a light scale/sharpen/deband finish.
            Name = "AI Upscale SD to HD (Real-ESRGAN)",
            Description = "Vintage SD to sharp 1080p with the Real-ESRGAN deep model (needs the realesrgan tool). Best for short/precious clips; slow.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 19,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 192,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            AiUpscaleModel = "x4plus",
            AiUpscaleHeight = 1080,
            Filters =
            [
                new() { FilterId = "deblock", Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",  Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
            ],
        },
        new()
        {
            // Over-the-air / DVR captures of film-sourced TV as broadcast by MeTV and similar
            // channels: MPEG-2 1280x720p59.94, the 24 fps film bob-upconverted to 60p, the 4:3 frame
            // pillarboxed with 88 px black bars. ivtc (bobbed) weaves the fields back and decimates
            // to an exact 23.976; the crop drops the bars BEFORE the upscale so the 1080p output is
            // all picture. Order is load-bearing: cadence and crop first (so every later filter sees
            // 4 frames in 5 and no black), clean before enlarging, sharpen and deband after. B&W
            // seasons strip the chroma, which on these captures is only MPEG-2 colour noise.
            // Adjust or remove the crop for a channel with different bars. Tuned on Gunsmoke.
            Name = "Broadcast 720p60 Film - B&W to 1080p",
            Description = "MeTV-style 720p59.94 broadcast capture of black-and-white film-sourced TV to 1080p23.976 HEVC 10-bit: crop 88 px pillarbars, inverse telecine, strip chroma noise, de-block, denoise, GPU neural upscale (falls back to spline without a Vulkan GPU), sharpen, deband.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 18,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 160,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            Filters =
            [
                new() { FilterId = "crop",      Parameters = new() { ["left"] = 88, ["right"] = 88 } },
                new() { FilterId = "ivtc",      Parameters = new() { ["bobbed"] = true, ["order"] = "tff" } },
                new() { FilterId = "grayscale", Parameters = new() },
                new() { FilterId = "deblock",   Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",    Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
                new() { FilterId = "superres",  Parameters = new() { ["model"] = "fsrcnnx", ["height"] = 1080 } },
                new() { FilterId = "cas",       Parameters = new() { ["strength"] = 0.4 } },
                new() { FilterId = "deband",    Parameters = new() { ["range"] = 16 } },
            ],
        },
        new()
        {
            Name = "Broadcast 720p60 Film - Colour to 1080p",
            Description = "MeTV-style 720p59.94 broadcast capture of colour film-sourced TV to 1080p23.976 HEVC 10-bit: crop 88 px pillarbars, inverse telecine, de-block, denoise, GPU neural upscale (falls back to spline without a Vulkan GPU), sharpen, deband.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 18,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 160,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            Filters =
            [
                new() { FilterId = "crop",      Parameters = new() { ["left"] = 88, ["right"] = 88 } },
                new() { FilterId = "ivtc",      Parameters = new() { ["bobbed"] = true, ["order"] = "tff" } },
                new() { FilterId = "deblock",   Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",    Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
                new() { FilterId = "superres",  Parameters = new() { ["model"] = "fsrcnnx", ["height"] = 1080 } },
                new() { FilterId = "cas",       Parameters = new() { ["strength"] = 0.4 } },
                new() { FilterId = "deband",    Parameters = new() { ["range"] = 16 } },
            ],
        },
        new()
        {
            // Premium variant: Real-ESRGAN instead of the GPU shader. The Filters are only the
            // pre-upscale chain; RealEsrganVideoProcessor adds the scale/CAS/deband finish. About
            // 30x slower than the GPU profile - for favourite episodes, not a 400-episode batch.
            Name = "Broadcast 720p60 Film - B&W to 1080p (Premium Real-ESRGAN)",
            Description = "MeTV-style 720p59.94 broadcast capture of black-and-white film-sourced TV to 1080p23.976 HEVC 10-bit: crop 88 px pillarbars, inverse telecine, strip chroma noise, de-block, denoise, Real-ESRGAN deep-model upscale (needs the realesrgan tool; slow, for favourite episodes), sharpen, deband.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 18,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 160,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            AiUpscaleModel = "x4plus",
            AiUpscaleHeight = 1080,
            Filters =
            [
                new() { FilterId = "crop",      Parameters = new() { ["left"] = 88, ["right"] = 88 } },
                new() { FilterId = "ivtc",      Parameters = new() { ["bobbed"] = true, ["order"] = "tff" } },
                new() { FilterId = "grayscale", Parameters = new() },
                new() { FilterId = "deblock",   Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",    Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
            ],
        },
        new()
        {
            Name = "Broadcast 720p60 Film - Colour to 1080p (Premium Real-ESRGAN)",
            Description = "MeTV-style 720p59.94 broadcast capture of colour film-sourced TV to 1080p23.976 HEVC 10-bit: crop 88 px pillarbars, inverse telecine, de-block, denoise, Real-ESRGAN deep-model upscale (needs the realesrgan tool; slow, for favourite episodes), sharpen, deband.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 18,
            VideoPreset = VideoPreset.Medium,
            BitDepth = VideoBitDepth.Ten,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 160,
            Container = ContainerFormat.Mkv,
            FilterEngineId = "ffmpeg",
            AiUpscaleModel = "x4plus",
            AiUpscaleHeight = 1080,
            Filters =
            [
                new() { FilterId = "crop",      Parameters = new() { ["left"] = 88, ["right"] = 88 } },
                new() { FilterId = "ivtc",      Parameters = new() { ["bobbed"] = true, ["order"] = "tff" } },
                new() { FilterId = "deblock",   Parameters = new() { ["quality"] = 3, ["strength"] = 4 } },
                new() { FilterId = "hqdn3d",    Parameters = new() { ["luma_spatial"] = 2.0, ["chroma_spatial"] = 1.5, ["luma_temporal"] = 3.0 } },
            ],
        },
        new()
        {
            Name = "Web - H.265 High Quality",
            Description = "High quality H.265 for web streaming. Opus audio.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 22,
            VideoPreset = VideoPreset.Slow,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 128,
            Container = ContainerFormat.Mkv,
        },
        new()
        {
            Name = "Web - H.264 Compatible",
            Description = "Widely compatible H.264 for maximum device support. AAC audio.",
            VideoEncoderId = "ffmpeg-x264",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 20,
            VideoPreset = VideoPreset.Medium,
            AudioEncoderId = "ffmpeg-audio-aac",
            AudioBitrate = 192,
            Container = ContainerFormat.Mp4,
        },
        new()
        {
            Name = "Archive - Lossless",
            Description = "Lossless FFV1 video with FLAC audio for archival.",
            VideoEncoderId = "ffmpeg-ffv1",
            VideoMode = VideoEncodingMode.ConstantQuality,
            VideoQuality = 0,
            VideoPreset = VideoPreset.Medium,
            AudioEncoderId = "ffmpeg-audio-flac",
            AudioBitrate = 0,
            Container = ContainerFormat.Mkv,
        },
        new()
        {
            Name = "YouTube Upload",
            Description = "Optimized for YouTube re-encoding. High bitrate H.264.",
            VideoEncoderId = "ffmpeg-x264",
            VideoMode = VideoEncodingMode.TwoPassBitrate,
            VideoBitrate = 15000,
            VideoPreset = VideoPreset.Slow,
            VideoTune = VideoTune.Film,
            AudioEncoderId = "ffmpeg-audio-aac",
            AudioBitrate = 320,
            Container = ContainerFormat.Mp4,
        },
        new()
        {
            Name = "Film - AV1 Efficient",
            Description = "Modern AV1 encoding for maximum compression efficiency.",
            VideoEncoderId = "ffmpeg-svtav1",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 30,
            VideoPreset = VideoPreset.Medium,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 128,
            Container = ContainerFormat.Mkv,
        },
        new()
        {
            Name = "Anime - H.265 Grain",
            Description = "Optimized for anime with grain preservation.",
            VideoEncoderId = "ffmpeg-x265",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 19,
            VideoPreset = VideoPreset.Slow,
            VideoTune = VideoTune.Animation,
            AudioEncoderId = "ffmpeg-audio-opus",
            AudioBitrate = 192,
            Container = ContainerFormat.Mkv,
        },
        new()
        {
            Name = "Fast Preview",
            Description = "Quick low-quality encode for preview purposes.",
            VideoEncoderId = "ffmpeg-x264",
            VideoMode = VideoEncodingMode.ConstantRateFactor,
            VideoQuality = 28,
            VideoPreset = VideoPreset.Ultrafast,
            AudioEncoderId = "ffmpeg-audio-aac",
            AudioBitrate = 96,
            Container = ContainerFormat.Mp4,
        },
    ];

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"unnamed_{DateTime.UtcNow.Ticks}";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(sanitized))
            return $"unnamed_{DateTime.UtcNow.Ticks}";
        // Cap length so a long profile name can't overflow the path limit.
        return sanitized.Length > 150 ? sanitized[..150] : sanitized;
    }
}
