using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Tools;

/// <summary>
/// Discovers, validates, and installs external tool prerequisites.
/// Checks configured paths, PATH environment, common install locations, and can auto-download.
/// </summary>
public sealed class ToolDiscovery
{
    private readonly ILogger<ToolDiscovery> _logger;
    private static readonly string ToolsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "moist", "tools");

    public ToolDiscovery(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<ToolDiscovery>() ?? NullLogger<ToolDiscovery>.Instance;
    }

    /// <summary>
    /// All known tool definitions that moist can use.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> KnownTools { get; } =
    [
        new ToolDefinition
        {
            Id = "ffmpeg", Name = "FFmpeg", DefaultBinaryName = "ffmpeg", IsRequired = true,
            Description = "Video/audio encoder, decoder, and muxer — the core engine",
            VersionArgs = "-version",
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
                ["linux-x64"] = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz",
            }
        },
        new ToolDefinition
        {
            Id = "ffprobe", Name = "FFprobe", DefaultBinaryName = "ffprobe", IsRequired = true,
            Description = "Media file analyzer — reads stream info, codecs, duration",
            VersionArgs = "-version",
            DownloadUrls = new() // Bundled with FFmpeg
            {
                ["win-x64"] = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
                ["linux-x64"] = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz",
            }
        },
        new ToolDefinition
        {
            Id = "mkvmerge", Name = "MKVToolNix (mkvmerge)", DefaultBinaryName = "mkvmerge", IsRequired = false,
            Description = "MKV muxer — advanced Matroska container support",
            VersionArgs = "--version",
        },
        new ToolDefinition
        {
            Id = "dovi_tool", Name = "dovi_tool (Dolby Vision)", DefaultBinaryName = "dovi_tool", IsRequired = false,
            Description = "Extracts and re-injects Dolby Vision RPUs, which a re-encode otherwise drops",
            VersionArgs = "--version",
        },
        new ToolDefinition
        {
            Id = "hdr10plus_tool", Name = "hdr10plus_tool", DefaultBinaryName = "hdr10plus_tool", IsRequired = false,
            Description = "Extracts and re-injects HDR10+ dynamic metadata, which a re-encode otherwise drops",
            VersionArgs = "--version",
        },
        new ToolDefinition
        {
            Id = "x264", Name = "x264 CLI", DefaultBinaryName = "x264", IsRequired = false,
            Description = "Standalone H.264 encoder for maximum control",
            VersionArgs = "--version",
        },
        new ToolDefinition
        {
            Id = "x265", Name = "x265 CLI", DefaultBinaryName = "x265", IsRequired = false,
            Description = "Standalone H.265/HEVC encoder for maximum control",
            VersionArgs = "--version",
        },
        new ToolDefinition
        {
            Id = "realesrgan", Name = "Real-ESRGAN (AI upscale)", DefaultBinaryName = "realesrgan-ncnn-vulkan", IsRequired = false,
            Description = "Deep-model super-resolution for the 'AI Upscale (Real-ESRGAN)' profile — Vulkan, cross-platform",
            VersionArgs = "-h",
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip",
                ["linux-x64"] = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-ubuntu.zip",
            }
        },
        new ToolDefinition
        {
            Id = "rife", Name = "RIFE (AI frame interpolation)", DefaultBinaryName = "rife-ncnn-vulkan", IsRequired = false,
            Description = "Deep-model frame interpolation for --fps-mode rife (24->60, slow-mo) — Vulkan, cross-platform",
            VersionArgs = "-h",
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip",
                ["linux-x64"] = "https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-ubuntu.zip",
            }
        },
        new ToolDefinition
        {
            Id = "vspipe", Name = "VapourSynth (vspipe)", DefaultBinaryName = "vspipe", IsRequired = false,
            Description = "VapourSynth pipe — required for VapourSynth filter scripts",
            VersionArgs = "--version",
        },
        new ToolDefinition
        {
            Id = "mp4box", Name = "MP4Box (GPAC)", DefaultBinaryName = "mp4box", IsRequired = false,
            Description = "MP4 container tool for advanced MP4 muxing",
            VersionArgs = "-version",
        },
    ];

    /// <summary>
    /// Check all known tools and return their status.
    /// Uses configured paths first, then PATH, then common locations.
    /// </summary>
    public async Task<List<ToolStatus>> CheckAllToolsAsync(
        Func<string, string>? getConfiguredPath = null,
        CancellationToken ct = default)
    {
        var results = new List<ToolStatus>();
        foreach (var tool in KnownTools)
        {
            ct.ThrowIfCancellationRequested();
            var status = await CheckToolAsync(tool, getConfiguredPath?.Invoke(tool.Id), ct);
            results.Add(status);
        }
        return results;
    }

    /// <summary>
    /// Check a single tool's availability.
    /// </summary>
    public async Task<ToolStatus> CheckToolAsync(ToolDefinition tool, string? configuredPath = null, CancellationToken ct = default)
    {
        var status = new ToolStatus { Tool = tool };

        // 1. Try configured path (if non-empty and not just the binary name)
        if (!string.IsNullOrWhiteSpace(configuredPath) && configuredPath != tool.DefaultBinaryName)
        {
            var result = await TryRunToolAsync(configuredPath, tool.VersionArgs, ct);
            if (result.Success)
            {
                status.IsAvailable = true;
                status.DetectedPath = configuredPath;
                status.Version = ParseVersion(result.Output);
                _logger.LogInformation("Tool {ToolId} found at configured path: {Path} (v{Version})",
                    tool.Id, configuredPath, status.Version);
                return status;
            }
            _logger.LogDebug("Tool {ToolId} not found at configured path: {Path}", tool.Id, configuredPath);
        }

        // 2. Try moist's own tools directory
        var moistToolPath = FindInMoistToolsDir(tool.DefaultBinaryName);
        if (moistToolPath is not null)
        {
            var result = await TryRunToolAsync(moistToolPath, tool.VersionArgs, ct);
            if (result.Success)
            {
                status.IsAvailable = true;
                status.DetectedPath = moistToolPath;
                status.Version = ParseVersion(result.Output);
                _logger.LogInformation("Tool {ToolId} found in moist tools dir: {Path}", tool.Id, moistToolPath);
                return status;
            }
        }

        // 3. Try PATH (using just the binary name)
        {
            var result = await TryRunToolAsync(tool.DefaultBinaryName, tool.VersionArgs, ct);
            if (result.Success)
            {
                status.IsAvailable = true;
                status.DetectedPath = tool.DefaultBinaryName; // On PATH
                status.Version = ParseVersion(result.Output);
                _logger.LogInformation("Tool {ToolId} found on PATH (v{Version})", tool.Id, status.Version);
                return status;
            }
        }

        // 4. Try common installation locations
        foreach (var commonPath in GetCommonPaths(tool.DefaultBinaryName))
        {
            if (!File.Exists(commonPath)) continue;
            var result = await TryRunToolAsync(commonPath, tool.VersionArgs, ct);
            if (result.Success)
            {
                status.IsAvailable = true;
                status.DetectedPath = commonPath;
                status.Version = ParseVersion(result.Output);
                _logger.LogInformation("Tool {ToolId} found at common location: {Path}", tool.Id, commonPath);
                return status;
            }
        }

        status.IsAvailable = false;
        status.Error = $"{tool.Name} not found. {(tool.IsRequired ? "This tool is required." : "Optional — some features may be unavailable.")}";
        _logger.LogWarning("Tool {ToolId} not found anywhere", tool.Id);
        return status;
    }

    /// <summary>
    /// Download and install a tool to the moist tools directory.
    /// Returns the path to the installed binary, or null on failure.
    /// </summary>
    public async Task<string?> DownloadAndInstallAsync(
        ToolDefinition tool,
        IProgress<(string Status, double Percent)>? progress = null,
        CancellationToken ct = default)
    {
        var platformKey = GetPlatformKey();
        if (!tool.DownloadUrls.TryGetValue(platformKey, out var url))
        {
            _logger.LogWarning("No download URL for {ToolId} on platform {Platform}", tool.Id, platformKey);
            progress?.Report(($"No download available for {tool.Name} on this platform.", -1));
            return null;
        }

        Directory.CreateDirectory(ToolsDir);
        var downloadPath = Path.Combine(ToolsDir, $"download_{tool.Id}_{Path.GetFileName(url)}");

        try
        {
            // Download
            progress?.Report(($"Downloading {tool.Name}...", 0));
            _logger.LogInformation("Downloading {ToolId} from {Url}", tool.Id, url);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(15);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("moist/1.0");

            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = File.Create(downloadPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (totalBytes > 0)
                {
                    var pct = (double)downloaded / totalBytes * 100;
                    progress?.Report(($"Downloading {tool.Name}... {downloaded / 1048576}MB / {totalBytes / 1048576}MB", pct));
                }
            }

            fileStream.Close();
            _logger.LogInformation("Downloaded {ToolId} ({Bytes} bytes)", tool.Id, downloaded);

            // Extract
            progress?.Report(($"Extracting {tool.Name}...", 95));
            var extractDir = Path.Combine(ToolsDir, tool.Id);
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(downloadPath, extractDir);
            }
            else if (downloadPath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
                  || downloadPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(extractDir);
                var tarProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "tar",
                    Arguments = $"xf \"{downloadPath}\" -C \"{extractDir}\" --strip-components=1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                });
                if (tarProcess is not null)
                {
                    await tarProcess.WaitForExitAsync(ct);
                    if (tarProcess.ExitCode != 0)
                    {
                        var err = await tarProcess.StandardError.ReadToEndAsync(ct);
                        _logger.LogError("tar extraction failed: {Error}", err);
                    }
                }
            }

            // Find the binary in the extracted directory
            var binaryName = tool.DefaultBinaryName;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                binaryName += ".exe";

            var foundBinary = FindBinaryInDirectory(extractDir, binaryName);
            if (foundBinary is null)
            {
                _logger.LogError("Could not find {Binary} in extracted archive at {Dir}", binaryName, extractDir);
                progress?.Report(($"Failed: Could not find {binaryName} in download.", -1));
                return null;
            }

            // On Linux/macOS, make executable
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("chmod", $"+x \"{foundBinary}\"")?.WaitForExit(5000);
            }

            progress?.Report(($"{tool.Name} installed successfully!", 100));
            _logger.LogInformation("Tool {ToolId} installed at {Path}", tool.Id, foundBinary);
            return foundBinary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to download/install {ToolId}", tool.Id);
            progress?.Report(($"Failed to install {tool.Name}: {ex.Message}", -1));
            return null;
        }
        finally
        {
            // Clean up download file
            try { if (File.Exists(downloadPath)) File.Delete(downloadPath); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Get the path to the moist tools directory.
    /// </summary>
    public static string GetToolsDirectory() => ToolsDir;

    private string? FindInMoistToolsDir(string binaryName)
    {
        if (!Directory.Exists(ToolsDir)) return null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            binaryName += ".exe";

        return FindBinaryInDirectory(ToolsDir, binaryName);
    }

    private static string? FindBinaryInDirectory(string directory, string binaryName)
    {
        if (!Directory.Exists(directory)) return null;

        // Check direct path
        var direct = Path.Combine(directory, binaryName);
        if (File.Exists(direct)) return direct;

        // Check bin/ subdirectory (common in extracted archives)
        var inBin = Path.Combine(directory, "bin", binaryName);
        if (File.Exists(inBin)) return inBin;

        // Recursive search (for archives with nested folders)
        try
        {
            var found = Directory.EnumerateFiles(directory, binaryName, SearchOption.AllDirectories)
                .FirstOrDefault();
            return found;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool Success, string Output)> TryRunToolAsync(
        string path, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return (false, "");

            // Read first few lines for version info — don't wait forever
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            // Many tools (ffmpeg, x265) write version to stderr
            var output = string.IsNullOrEmpty(stdout) ? stderr : stdout;
            return (true, output);
        }
        catch
        {
            return (false, "");
        }
    }

    private static string ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "unknown";

        // Try to find version-like patterns in the first line
        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        // Common patterns: "ffmpeg version N6-...", "x264 0.164.x", "mkvmerge v82.0"
        // Just return the first line trimmed, up to 80 chars
        return firstLine.Length > 80 ? firstLine[..80] : firstLine.Trim();
    }

    private static IEnumerable<string> GetCommonPaths(string binaryName)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var ext = isWindows ? ".exe" : "";
        var binary = binaryName + ext;

        if (isWindows)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(programFiles, "FFmpeg", "bin", binary);
            yield return Path.Combine(programFiles, "ffmpeg", "bin", binary);
            yield return Path.Combine(programFilesX86, "FFmpeg", "bin", binary);
            yield return Path.Combine(localAppData, "FFmpeg", "bin", binary);
            yield return Path.Combine(@"C:\ffmpeg\bin", binary);
            yield return Path.Combine(@"C:\tools\ffmpeg\bin", binary);
            yield return Path.Combine(programFiles, "MKVToolNix", binary);
            yield return Path.Combine(programFilesX86, "MKVToolNix", binary);
            yield return Path.Combine(programFiles, "VapourSynth", "core", binary);
        }
        else
        {
            yield return Path.Combine("/usr/bin", binary);
            yield return Path.Combine("/usr/local/bin", binary);
            yield return Path.Combine("/opt/homebrew/bin", binary);
            yield return Path.Combine("/snap/bin", binary);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".local", "bin", binary);
        }
    }

    private static string GetPlatformKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return "unknown";
    }
}
