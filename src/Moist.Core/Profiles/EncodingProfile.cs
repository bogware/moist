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
    public string VideoCustomArgs { get; set; } = string.Empty;

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
        File.WriteAllText(path, json);
    }

    public async Task SaveAsync(EncodingProfile profile, CancellationToken ct = default)
    {
        profile.ModifiedAt = DateTime.UtcNow;
        var fileName = SanitizeFileName(profile.Name) + ".json";
        var path = Path.Combine(_profilesDir, fileName);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
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
            if (!File.Exists(path))
                Save(profile);
        }
    }

    public static List<EncodingProfile> GetBuiltInProfiles() =>
    [
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
        return string.IsNullOrEmpty(sanitized) ? $"unnamed_{DateTime.UtcNow.Ticks}" : sanitized;
    }
}
