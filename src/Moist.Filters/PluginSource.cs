using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moist.Core.Settings;

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

    /// <summary>
    /// Direct https download link to the plugin archive/binary. Empty when the plugin has to be
    /// installed manually — in that case <see cref="Website"/> points at the project page.
    /// </summary>
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>
    /// Project / documentation page for the plugin, opened in the browser when there is no
    /// direct <see cref="DownloadUrl"/> so the user can fetch it manually.
    /// </summary>
    public string Website { get; init; } = string.Empty;

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
        _configDir = MoistPaths.ConfigRoot;
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
            if (plugin.InstalledPath is not null)
            {
                try
                {
                    if (Directory.Exists(plugin.InstalledPath))
                        Directory.Delete(plugin.InstalledPath, recursive: true);
                    else if (File.Exists(plugin.InstalledPath))
                        File.Delete(plugin.InstalledPath);
                }
                catch { /* best-effort cleanup */ }
            }
            Save();
        }
    }

    /// <summary>
    /// Downloads a plugin that carries a direct <see cref="PluginPackage.DownloadUrl"/> into a
    /// per-plugin folder under <see cref="PluginsDirectory"/>. A <c>.zip</c> is extracted in place.
    /// </summary>
    /// <remarks>
    /// Security: only <c>https</c> URLs are fetched, and nothing is executed here — moist loads
    /// <c>IPlugin</c> DLLs from the plugins directory only at startup, so a freshly installed plugin
    /// takes effect after a restart. This is best-effort: on any failure it reports a message through
    /// <paramref name="progress"/> and returns <c>null</c> rather than throwing, and any folder it
    /// created for a failed install is removed.
    /// </remarks>
    /// <param name="pkg">The package to install; must have a non-empty <see cref="PluginPackage.DownloadUrl"/>.</param>
    /// <param name="progress">Optional status/percentage sink for UI feedback (percentage of -1 signals a failure).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The local install directory, or <c>null</c> if nothing was downloaded.</returns>
    public async Task<string?> DownloadPluginAsync(
        PluginPackage pkg,
        IProgress<(string Status, double Percent)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pkg.DownloadUrl))
            return null;

        // Only ever fetch over HTTPS: the file lands in a directory moist scans for plugin DLLs at
        // startup, so the transport must be authenticated. Anything else is refused outright.
        if (!Uri.TryCreate(pkg.DownloadUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(($"Refusing to download {pkg.Name}: only https:// links are allowed.", -1));
            return null;
        }

        var targetDir = Path.Combine(_pluginsDir, SanitizeFolderName(pkg.Id));
        var dirExisted = Directory.Exists(targetDir);

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = SanitizeFolderName(pkg.Id);
        var downloadPath = Path.Combine(targetDir, fileName);

        try
        {
            Directory.CreateDirectory(targetDir);

            progress?.Report(($"Downloading {pkg.Name}...", 0));

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(15);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("moist/1.0");

            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = File.Create(downloadPath))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    if (totalBytes > 0)
                    {
                        var pct = (double)downloaded / totalBytes * 100;
                        progress?.Report((
                            $"Downloading {pkg.Name}... {downloaded / 1048576}MB / {totalBytes / 1048576}MB",
                            pct));
                    }
                }
            }

            // Unpack an archive in place so a plugin shipped as a .zip lands as loose files that
            // moist's startup scan can pick up. Non-archive payloads (a bare .dll) are left as-is.
            if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(($"Extracting {pkg.Name}...", 97));
                ZipFile.ExtractToDirectory(downloadPath, targetDir, overwriteFiles: true);
                try { File.Delete(downloadPath); } catch { /* best-effort */ }
            }

            progress?.Report(($"Installed {pkg.Name} — restart moist to load it.", 100));
            return targetDir;
        }
        catch (Exception ex)
        {
            progress?.Report(($"Failed to install {pkg.Name}: {ex.Message}", -1));
            // Don't leave a half-written install behind, but never clobber a folder we didn't create.
            if (!dirExisted)
            {
                try { if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true); }
                catch { /* best-effort */ }
            }
            return null;
        }
    }

    /// <summary>Strips characters that aren't valid in a folder name so a package id is safe to use as a path segment.</summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "plugin" : cleaned;
    }

    /// <summary>
    /// Gets the online marketplace catalog of installable filter plugins.
    /// Returns a curated list of well-known FFmpeg / VapourSynth / AviSynth+ ecosystem plugins.
    /// Entries carry a <see cref="PluginPackage.Website"/> project page for manual installs;
    /// a <see cref="PluginPackage.DownloadUrl"/> is set only where a direct https archive is known,
    /// otherwise the plugin is installed by opening its page. The task is async so a future remote
    /// index can be fetched here and fall back to this curated list on failure.
    /// </summary>
    public Task<IReadOnlyList<PluginPackage>> GetOnlineCatalogAsync(CancellationToken ct = default)
        => Task.FromResult(GetCuratedCatalog());

    /// <summary>
    /// The built-in, hand-maintained catalog of well-known filter plugins. Kept separate so a
    /// remote fetch in <see cref="GetOnlineCatalogAsync"/> can fall back to it and the tab is
    /// never empty.
    /// </summary>
    private static IReadOnlyList<PluginPackage> GetCuratedCatalog()
    {
        // Most entries leave DownloadUrl empty on purpose: these plugins ship platform-specific
        // release assets whose exact filenames change every version, so we point the user at the
        // project page (Website) rather than pin a link that would 404. The real downloader is
        // exercised by any entry (or user-added source) that does carry a direct https archive.
        return new List<PluginPackage>
        {
            // VapourSynth plugins
            new() { Id = "vs-bm3dcuda", Name = "BM3D CUDA", Version = "2.1", Engine = "vapoursynth", Category = "Denoise", Author = "WolframRhodium", Description = "GPU-accelerated BM3D denoising via CUDA", Website = "https://github.com/WolframRhodium/VapourSynth-BM3DCUDA" },
            new() { Id = "vs-knlmeanscl", Name = "KNLMeans CL", Version = "1.1.1", Engine = "vapoursynth", Category = "Denoise", Author = "Khanattila", Description = "OpenCL Non-Local Means denoiser", Website = "https://github.com/Khanattila/KNLMeansCL" },
            new() { Id = "vs-dfttest", Name = "DFTTest", Version = "7.0", Engine = "vapoursynth", Category = "Denoise", Author = "HolyWu", Description = "2D/3D frequency domain denoiser", Website = "https://github.com/HomeOfVapourSynthEvolution/VapourSynth-DFTTest" },
            new() { Id = "vs-fmtconv", Name = "fmtconv", Version = "30", Engine = "vapoursynth", Category = "Resize", Author = "EleonoreMizo", Description = "High-quality format conversion and resampling", Website = "https://github.com/EleonoreMizo/fmtconv" },
            new() { Id = "vs-nnedi3", Name = "NNEDI3", Version = "12", Engine = "vapoursynth", Category = "Deinterlace", Author = "dubhater", Description = "Neural network edge directed interpolation", Website = "https://github.com/dubhater/vapoursynth-nnedi3" },
            new() { Id = "vs-nnedi3cl", Name = "NNEDI3CL", Version = "8", Engine = "vapoursynth", Category = "Deinterlace", Author = "HolyWu", Description = "OpenCL-accelerated NNEDI3", Website = "https://github.com/HomeOfVapourSynthEvolution/VapourSynth-NNEDI3CL" },
            new() { Id = "vs-eedi3", Name = "EEDI3", Version = "2.0", Engine = "vapoursynth", Category = "Deinterlace", Author = "HolyWu", Description = "Enhanced edge directed interpolation v3", Website = "https://github.com/HomeOfVapourSynthEvolution/VapourSynth-EEDI3" },
            new() { Id = "vs-descale", Name = "Descale", Version = "8", Engine = "vapoursynth", Category = "Resize", Author = "Irrational-Encoding-Wizardry", Description = "Undo upscaling with various algorithms", Website = "https://github.com/Irrational-Encoding-Wizardry/descale" },
            new() { Id = "vs-mvtools", Name = "MVTools", Version = "24", Engine = "vapoursynth", Category = "FrameRate", Author = "dubhater", Description = "Motion compensation based tools", Website = "https://github.com/dubhater/vapoursynth-mvtools" },
            new() { Id = "vs-rife", Name = "RIFE ncnn Vulkan", Version = "5.0", Engine = "vapoursynth", Category = "FrameRate", Author = "HolyWu", Description = "AI-based frame interpolation (ncnn/Vulkan)", Website = "https://github.com/HomeOfVapourSynthEvolution/VapourSynth-RIFE-ncnn-Vulkan" },
            new() { Id = "vs-cas", Name = "CAS (Sharpener)", Version = "2.0", Engine = "vapoursynth", Category = "Sharpen", Author = "HolyWu", Description = "AMD FidelityFX Contrast Adaptive Sharpening", Website = "https://github.com/HomeOfVapourSynthEvolution/VapourSynth-CAS" },
            new() { Id = "vs-waifu2x", Name = "Waifu2x", Version = "6.0", Engine = "vapoursynth", Category = "Resize", Author = "HolyWu", Description = "AI-powered anime upscaling", Website = "https://github.com/HomeOfVapourSynthEvolution/VapourSynth-Waifu2x-caffe" },
            new() { Id = "vs-mlrt", Name = "vs-mlrt", Version = "15.8", Engine = "vapoursynth", Category = "Resize", Author = "AmusementClub", Description = "ML runtime (ORT/OV/TRT) for Real-ESRGAN/waifu2x super-resolution", Website = "https://github.com/AmusementClub/vs-mlrt" },
            new() { Id = "vs-placebo", Name = "vs-placebo", Version = "1.4.4", Engine = "vapoursynth", Category = "Custom", Author = "Lypheo", Description = "libplacebo bindings: debanding, tone mapping, shaders", Website = "https://github.com/sgt0/vs-placebo" },
            new() { Id = "vs-akarin", Name = "vs-akarin", Version = "0.96", Engine = "vapoursynth", Category = "Custom", Author = "AkarinVS", Description = "Expr/Select/Version utilities with a JIT expression engine", Website = "https://github.com/AkarinVS/vapoursynth-plugin" },
            new() { Id = "vs-remap", Name = "Remap", Version = "4.0", Engine = "vapoursynth", Category = "Custom", Author = "Irrational-Encoding-Wizardry", Description = "Frame remapping/timeline editing", Website = "https://github.com/Irrational-Encoding-Wizardry/Vapoursynth-VSTools" },

            // AviSynth plugins
            new() { Id = "avs-nnedi3", Name = "NNEDI3", Version = "0.9.4.61", Engine = "avisynth", Category = "Deinterlace", Author = "jpsdr", Description = "Neural network edge directed interpolation for AviSynth+", Website = "https://github.com/jpsdr/NNEDI3" },
            new() { Id = "avs-mvtools2", Name = "MVTools2", Version = "2.7.46", Engine = "avisynth", Category = "FrameRate", Author = "pinterf", Description = "Motion compensation tools for AviSynth+", Website = "https://github.com/pinterf/mvtools" },
            new() { Id = "avs-fft3dfilter", Name = "FFT3DFilter", Version = "2.6", Engine = "avisynth", Category = "Denoise", Author = "pinterf", Description = "3D frequency domain denoiser", Website = "https://github.com/pinterf/fft3dfilter" },
            new() { Id = "avs-dfttest", Name = "DFTTest", Version = "1.9.7", Engine = "avisynth", Category = "Denoise", Author = "pinterf", Description = "2D/3D frequency domain denoiser", Website = "https://github.com/pinterf/dfttest" },
            new() { Id = "avs-removegrain", Name = "RgTools", Version = "1.2", Engine = "avisynth", Category = "Denoise", Author = "pinterf", Description = "RemoveGrain/Repair fast spatial denoiser", Website = "https://github.com/pinterf/RgTools" },
            new() { Id = "avs-qtgmc", Name = "QTGMC", Version = "3.388", Engine = "avisynth", Category = "Deinterlace", Author = "Vit / community", Description = "The reference high-quality deinterlacer script", Website = "http://avisynth.nl/index.php/QTGMC" },
            new() { Id = "avs-finesharp", Name = "FineSharp", Version = "2020", Engine = "avisynth", Category = "Sharpen", Author = "Didee", Description = "High-quality sharpening script", Website = "http://avisynth.nl/index.php/FineSharp" },

            // FFmpeg ecosystem
            new() { Id = "ff-libplacebo", Name = "libplacebo (FFmpeg)", Version = "7.349", Engine = "ffmpeg", Category = "Resize", Author = "haasn", Description = "GPU-accelerated scaling, tone mapping, HDR", Website = "https://code.videolan.org/videolan/libplacebo" },
            new() { Id = "ff-zscale", Name = "zscale (FFmpeg)", Version = "3.0", Engine = "ffmpeg", Category = "Resize", Author = "sekrit-twc", Description = "High-quality image scaling via zimg", Website = "https://github.com/sekrit-twc/zimg" },
            new() { Id = "ff-vmaf", Name = "libvmaf (FFmpeg)", Version = "3.0", Engine = "ffmpeg", Category = "Custom", Author = "Netflix", Description = "Perceptual video quality metric (VMAF)", Website = "https://github.com/Netflix/vmaf" },
        };
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
