using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moist.Filters;

/// <summary>
/// Represents a source location for filter/plugin files.
/// Can be a local directory or an online repository.
/// </summary>
public sealed class PluginSource
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required PluginSourceType Type { get; init; }

    /// <summary>
    /// Local directory path, or base URL for online sources.
    /// </summary>
    public required string Location { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string Description { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public enum PluginSourceType
{
    LocalDirectory,
    OnlineRepository,
}

/// <summary>
/// Represents an installable plugin package from an online source.
/// </summary>
public sealed class PluginPackage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Engine { get; init; } = string.Empty; // ffmpeg, vapoursynth, avisynth
    public string DownloadUrl { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsInstalled { get; set; }
    public string? InstalledPath { get; set; }
}

/// <summary>
/// Manages plugin sources (local directories and online repositories)
/// and installed plugins. Persists configuration as JSON.
/// </summary>
public sealed class PluginSourceManager
{
    private readonly List<PluginSource> _sources = [];
    private readonly List<PluginPackage> _installedPlugins = [];
    private readonly string _configDir;
    private readonly string _pluginsDir;

    public IReadOnlyList<PluginSource> Sources => _sources;
    public IReadOnlyList<PluginPackage> InstalledPlugins => _installedPlugins;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public PluginSourceManager()
    {
        _configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "moist");
        _pluginsDir = Path.Combine(_configDir, "plugins");
        Directory.CreateDirectory(_pluginsDir);

        Load();
        if (_sources.Count == 0)
            AddDefaultSources();
    }

    public string PluginsDirectory => _pluginsDir;

    private void AddDefaultSources()
    {
        // Default online sources for popular filter/plugin repos
        _sources.Add(new PluginSource
        {
            Id = "vs-plugins-github",
            Name = "VapourSynth Plugins (GitHub)",
            Type = PluginSourceType.OnlineRepository,
            Location = "https://github.com/vapoursynth/vs-plugins",
            Description = "Community VapourSynth plugins repository",
        });

        _sources.Add(new PluginSource
        {
            Id = "avs-plugins",
            Name = "AviSynth+ Plugins",
            Type = PluginSourceType.OnlineRepository,
            Location = "https://forum.doom9.org/showthread.php?t=175823",
            Description = "AviSynth+ community plugins and scripts",
        });

        _sources.Add(new PluginSource
        {
            Id = "local-plugins",
            Name = "Local Plugins",
            Type = PluginSourceType.LocalDirectory,
            Location = _pluginsDir,
            Description = "User-installed local plugins",
        });

        Save();
    }

    public void AddSource(PluginSource source)
    {
        if (source.Type == PluginSourceType.LocalDirectory && !Directory.Exists(source.Location))
            throw new DirectoryNotFoundException($"Plugin directory not found: {source.Location}");

        if (_sources.Any(s => s.Id == source.Id))
            throw new InvalidOperationException($"Plugin source with ID '{source.Id}' already exists.");

        _sources.Add(source);
        Save();
    }

    public void RemoveSource(string id)
    {
        _sources.RemoveAll(s => s.Id == id);
        Save();
    }

    public void UpdateSource(PluginSource source)
    {
        var idx = _sources.FindIndex(s => s.Id == source.Id);
        if (idx >= 0)
        {
            _sources[idx] = source;
            Save();
        }
    }

    public void AddInstalledPlugin(PluginPackage package)
    {
        package.IsInstalled = true;
        _installedPlugins.Add(package);
        Save();
    }

    public void RemoveInstalledPlugin(string id)
    {
        var plugin = _installedPlugins.FirstOrDefault(p => p.Id == id);
        if (plugin is not null)
        {
            _installedPlugins.Remove(plugin);
            if (plugin.InstalledPath is not null && File.Exists(plugin.InstalledPath))
                File.Delete(plugin.InstalledPath);
            Save();
        }
    }

    /// <summary>
    /// Gets the built-in online marketplace catalog.
    /// In production, this would fetch from the actual URLs.
    /// For now, returns a curated catalog of well-known plugins.
    /// </summary>
    public Task<IReadOnlyList<PluginPackage>> GetOnlineCatalogAsync(CancellationToken ct = default)
    {
        // Built-in catalog of well-known filter plugins
        IReadOnlyList<PluginPackage> catalog = new List<PluginPackage>
        {
            // VapourSynth plugins
            new() { Id = "vs-bm3dcuda", Name = "BM3D CUDA", Version = "2.1", Engine = "vapoursynth", Category = "Denoise", Author = "WolframRhodworking", Description = "GPU-accelerated BM3D denoising via CUDA" },
            new() { Id = "vs-knlmeanscl", Name = "KNLMeans CL", Version = "1.1.1", Engine = "vapoursynth", Category = "Denoise", Author = "Khanattila", Description = "OpenCL Non-Local Means denoiser" },
            new() { Id = "vs-dfttest", Name = "DFTTest", Version = "7.0", Engine = "vapoursynth", Category = "Denoise", Author = "HolyWu", Description = "2D/3D frequency domain denoiser" },
            new() { Id = "vs-fmtconv", Name = "fmtconv", Version = "30", Engine = "vapoursynth", Category = "Resize", Author = "EleonoreMizo", Description = "High-quality format conversion and resampling" },
            new() { Id = "vs-nnedi3", Name = "NNEDI3", Version = "12", Engine = "vapoursynth", Category = "Deinterlace", Author = "dubhater", Description = "Neural network edge directed interpolation" },
            new() { Id = "vs-nnedi3cl", Name = "NNEDI3CL", Version = "8", Engine = "vapoursynth", Category = "Deinterlace", Author = "HolyWu", Description = "OpenCL-accelerated NNEDI3" },
            new() { Id = "vs-eedi3", Name = "EEDI3", Version = "2.0", Engine = "vapoursynth", Category = "Deinterlace", Author = "HolyWu", Description = "Enhanced edge directed interpolation v3" },
            new() { Id = "vs-descale", Name = "Descale", Version = "8", Engine = "vapoursynth", Category = "Resize", Author = "Irrational-Encoding-Wizardry", Description = "Undo upscaling with various algorithms" },
            new() { Id = "vs-mvtools", Name = "MVTools", Version = "24", Engine = "vapoursynth", Category = "FrameRate", Author = "dubhater", Description = "Motion compensation based tools" },
            new() { Id = "vs-rife", Name = "RIFE", Version = "5.0", Engine = "vapoursynth", Category = "FrameRate", Author = "HomeOfVapourSynthEvolution", Description = "AI-based frame interpolation" },
            new() { Id = "vs-cas", Name = "CAS (Sharpener)", Version = "2.0", Engine = "vapoursynth", Category = "Sharpen", Author = "HomeOfVapourSynthEvolution", Description = "AMD FidelityFX Contrast Adaptive Sharpening" },
            new() { Id = "vs-waifu2x", Name = "Waifu2x", Version = "6.0", Engine = "vapoursynth", Category = "Resize", Author = "HomeOfVapourSynthEvolution", Description = "AI-powered anime upscaling" },
            new() { Id = "vs-remap", Name = "Remap", Version = "4.0", Engine = "vapoursynth", Category = "Custom", Author = "Irrational-Encoding-Wizardry", Description = "Frame remapping/timeline editing" },

            // AviSynth plugins
            new() { Id = "avs-nnedi3", Name = "NNEDI3", Version = "0.9.4.61", Engine = "avisynth", Category = "Deinterlace", Author = "jpsdr", Description = "Neural network edge directed interpolation for AviSynth+" },
            new() { Id = "avs-mvtools2", Name = "MVTools2", Version = "2.7.46", Engine = "avisynth", Category = "FrameRate", Author = "pinterf", Description = "Motion compensation tools for AviSynth+" },
            new() { Id = "avs-fft3dfilter", Name = "FFT3DFilter", Version = "2.6", Engine = "avisynth", Category = "Denoise", Author = "pinterf", Description = "3D frequency domain denoiser" },
            new() { Id = "avs-dfttest", Name = "DFTTest", Version = "1.9.7", Engine = "avisynth", Category = "Denoise", Author = "pinterf", Description = "2D/3D frequency domain denoiser" },
            new() { Id = "avs-removegrain", Name = "RemoveGrain/Repair", Version = "1.0.1", Engine = "avisynth", Category = "Denoise", Author = "pinterf", Description = "Fast spatial denoiser with repair" },
            new() { Id = "avs-finesharp", Name = "FineSharp", Version = "2020", Engine = "avisynth", Category = "Sharpen", Author = "Didee", Description = "High-quality sharpening script" },

            // FFmpeg ecosystem
            new() { Id = "ff-libplacebo", Name = "libplacebo (FFmpeg)", Version = "7.349", Engine = "ffmpeg", Category = "Resize", Author = "haasn", Description = "GPU-accelerated scaling, tone mapping, HDR" },
            new() { Id = "ff-zscale", Name = "zscale (FFmpeg)", Version = "3.0", Engine = "ffmpeg", Category = "Resize", Author = "sekrit-twc", Description = "High-quality image scaling via zimg" },
        };

        return Task.FromResult(catalog);
    }

    private void Save()
    {
        Directory.CreateDirectory(_configDir);
        var data = new PluginSourceData { Sources = _sources, InstalledPlugins = _installedPlugins };
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(GetConfigPath(), json);
    }

    private void Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<PluginSourceData>(json, JsonOptions);
            if (data is not null)
            {
                _sources.AddRange(data.Sources);
                _installedPlugins.AddRange(data.InstalledPlugins);
            }
        }
        catch
        {
            // Corrupted config — start fresh
        }
    }

    private string GetConfigPath() => Path.Combine(_configDir, "plugin-sources.json");

    private sealed class PluginSourceData
    {
        public List<PluginSource> Sources { get; set; } = [];
        public List<PluginPackage> InstalledPlugins { get; set; } = [];
    }
}
