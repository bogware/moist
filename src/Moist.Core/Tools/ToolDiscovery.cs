using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Settings;

namespace Moist.Core.Tools;

/// <summary>
/// Discovers, validates, and installs external tool prerequisites.
/// Checks configured paths, PATH environment, common install locations, and can auto-download.
/// </summary>
public sealed class ToolDiscovery
{
    private readonly ILogger<ToolDiscovery> _logger;
    private static readonly string ToolsDir = Path.Combine(MoistPaths.ConfigRoot, "tools");

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
            // Pinned to immutable versioned builds (NOT BtbN's rolling `latest` tag): the served
            // bytes stay constant, so the recorded SHA-256 stays valid. Windows uses gyan.dev's
            // versioned release; Linux uses BtbN's release-branch build from a MONTH-END autobuild —
            // BtbN deletes its daily autobuilds after a few weeks (a daily pin 404'd) but keeps the
            // last build of each month. To update: pick a month-end tag, download each artifact,
            // hash it (`sha256sum <file>` / `Get-FileHash -Algorithm SHA256 <file>`), update both.
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build.zip",
                ["linux-x64"] = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-linux64-gpl-9.0.tar.xz",
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "759d0a9831c436a0eb331ad36f236c06cb04aaa0005da46571f0e9d3d9206f6b",
                ["linux-x64"] = "73e29a56974ae5a651133a04584c7483b21afe941e17dc91d79814a758b608d3",
            },
        },
        new ToolDefinition
        {
            Id = "ffprobe", Name = "FFprobe", DefaultBinaryName = "ffprobe", IsRequired = true,
            Description = "Media file analyzer — reads stream info, codecs, duration",
            VersionArgs = "-version",
            // ffprobe ships inside the same FFmpeg archive; pinned identically to the ffmpeg entry
            // above so both come from the same immutable build. See that entry for hash-update steps.
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-full_build.zip",
                ["linux-x64"] = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-linux64-gpl-9.0.tar.xz",
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "759d0a9831c436a0eb331ad36f236c06cb04aaa0005da46571f0e9d3d9206f6b",
                ["linux-x64"] = "73e29a56974ae5a651133a04584c7483b21afe941e17dc91d79814a758b608d3",
            },
        },
        new ToolDefinition
        {
            Id = "mkvmerge", Name = "MKVToolNix (mkvmerge)", DefaultBinaryName = "mkvmerge", IsRequired = false,
            Description = "MKV muxer — advanced Matroska container support",
            VersionArgs = "--version",
            // Windows: the official portable 7z (LZMA), unpacked in-process by SharpCompress — the
            // tar.exe that ships with Windows can't decode LZMA. The hash matches the .sha256 file
            // published beside it. Linux has no single-binary build (an AppImage or distro package),
            // so it stays a "Get" link: `apt install mkvtoolnix` / `dnf install mkvtoolnix`.
            // Used by the standalone x265 encoder to wrap B-frame streams with correct timestamps.
            DownloadUrls = new()
            {
                ["win-x64"] = "https://mkvtoolnix.download/windows/releases/103.0/mkvtoolnix-64-bit-103.0.7z",
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "01d76dbfdbd5615d9e02f69b0710d1429a902d47a98c8eee33bd884c4fcb3cbf",
            },
            HelpUrl = "https://mkvtoolnix.download/downloads.html",
        },
        new ToolDefinition
        {
            Id = "dovi_tool", Name = "dovi_tool (Dolby Vision)", DefaultBinaryName = "dovi_tool", IsRequired = false,
            Description = "Extracts and re-injects Dolby Vision RPUs, which a re-encode otherwise drops",
            VersionArgs = "--version",
            // Clean single-binary GitHub releases from quietvoid/dovi_tool. Pinned to an immutable
            // versioned tag (NOT a rolling "latest") so a recorded SHA-256 stays valid: win = the
            // *-x86_64-pc-windows-msvc.zip asset, linux = the *-x86_64-unknown-linux-musl.tar.gz asset.
            // Version: 2.1.3. To update: bump the tag in both URLs, download each asset, hash it
            // (`Get-FileHash -Algorithm SHA256` / `sha256sum`), and record the hex in ExpectedSha256.
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/quietvoid/dovi_tool/releases/download/2.1.3/dovi_tool-2.1.3-x86_64-pc-windows-msvc.zip",
                ["linux-x64"] = "https://github.com/quietvoid/dovi_tool/releases/download/2.1.3/dovi_tool-2.1.3-x86_64-unknown-linux-musl.tar.gz",
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "0b8742cabc29555f5bd128efce9c869aa96078d5a96de2de5d15dd4539ed9da5",
                ["linux-x64"] = "3933a4ea48970ba63dc007e1889791343ab8dea7f46492554cec5da5aad8c13e",
            },
        },
        new ToolDefinition
        {
            Id = "hdr10plus_tool", Name = "hdr10plus_tool", DefaultBinaryName = "hdr10plus_tool", IsRequired = false,
            Description = "Extracts and re-injects HDR10+ dynamic metadata, which a re-encode otherwise drops",
            VersionArgs = "--version",
            // Clean single-binary GitHub releases from quietvoid/hdr10plus_tool, same asset-naming
            // pattern and pinning rules as dovi_tool above. Version: 1.7.0. To update: bump the tag,
            // download each asset, hash it, and record the hex in ExpectedSha256.
            DownloadUrls = new()
            {
                ["win-x64"] = "https://github.com/quietvoid/hdr10plus_tool/releases/download/1.7.0/hdr10plus_tool-1.7.0-x86_64-pc-windows-msvc.zip",
                ["linux-x64"] = "https://github.com/quietvoid/hdr10plus_tool/releases/download/1.7.0/hdr10plus_tool-1.7.0-x86_64-unknown-linux-musl.tar.gz",
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "9ed0a18efb5dce5e30770a801d5846b9ef165605c576762873c0ae290ccee784",
                ["linux-x64"] = "ca03c9cb4a10e7d6407899c178d867610dbe13b01616abd587dab09efc593d3f",
            },
        },
        new ToolDefinition
        {
            Id = "x264", Name = "x264 CLI", DefaultBinaryName = "x264", IsRequired = false,
            Description = "Standalone H.264 encoder for maximum control",
            VersionArgs = "--version",
            // No canonical single-file cross-platform binary release; users grab a build for their OS.
            // Offer the VideoLAN developer page rather than an auto-download.
            HelpUrl = "https://www.videolan.org/developers/x264.html",
        },
        new ToolDefinition
        {
            Id = "x265", Name = "x265 CLI", DefaultBinaryName = "x265", IsRequired = false,
            Description = "Standalone H.265/HEVC encoder for maximum control",
            VersionArgs = "--version",
            // As with x264: no clean drop-in binary release, so point at the VideoLAN developer page.
            HelpUrl = "https://www.videolan.org/developers/x265.html",
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
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d",
                ["linux-x64"] = "e5aa6eb131234b87c0c51f82b89390f5e3e642b7b70f2b9bbe95b6a285a40c96",
            },
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
            },
            ExpectedSha256 = new()
            {
                ["win-x64"] = "d8e4d772d26cd8006ef0ad0bc82eb191b53c68677d1ae2f42506d74cbbbea606",
                ["linux-x64"] = "1e2c7ee7fa7daa326542d50622f0afedc80cf6f1858bda411d16385ffa5cdf68",
            },
        },
        new ToolDefinition
        {
            Id = "vspipe", Name = "VapourSynth (vspipe)", DefaultBinaryName = "vspipe", IsRequired = false,
            // VapourSynth is a Python package, not a drop-in binary — the "portable" is a pip
            // bootstrap (vspipe.bat + pip.bat + wheel), so it's detection-only: install VapourSynth
            // with its own installer, then moist finds vspipe and can pipe .vpy scripts to FFmpeg.
            // No DownloadUrls; the "Get" button points at the official installation guide instead.
            Description = "VapourSynth pipe — runs .vpy scripts (incl. vs-mlrt ML nodes) for the VapourSynth engine",
            VersionArgs = "--version",
            HelpUrl = "https://www.vapoursynth.com/doc/installation.html",
        },
        new ToolDefinition
        {
            Id = "mp4box", Name = "MP4Box (GPAC)", DefaultBinaryName = "mp4box", IsRequired = false,
            Description = "MP4 container tool for advanced MP4 muxing",
            VersionArgs = "-version",
            // GPAC ships as a platform installer, not a single drop-in binary — point at the GPAC
            // downloads page for a one-click "Get" rather than auto-downloading.
            HelpUrl = "https://gpac.io/downloads/gpac-nightly-builds/",
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

        // The payload is executed as a subprocess for every subsequent encode, so refuse any
        // non-HTTPS transport — a plaintext download is trivially MITM-able.
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Refusing to download {ToolId}: URL is not HTTPS ({Url})", tool.Id, url);
            progress?.Report(($"Refusing to download {tool.Name}: download URL is not HTTPS.", -1));
            return null;
        }

        // The downloaded archive is verified against a pinned SHA-256 (see below, after the
        // download completes) before it is extracted and its binary executed. URLs are pinned to
        // immutable builds so a recorded hash stays valid; a tool/platform with no recorded hash
        // yet installs with a loud "unverified download" warning rather than silently.
        var expectedHash = tool.ExpectedSha256 is not null
            && tool.ExpectedSha256.TryGetValue(platformKey, out var configuredHash)
            && !string.IsNullOrWhiteSpace(configuredHash)
                ? configuredHash.Trim()
                : null;

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

            // Integrity check — BEFORE extraction, because extraction writes the archive's contents
            // to disk and we then execute the extracted binary for every encode. Compute the hash of
            // the file on disk regardless, so a maintainer can copy it into ExpectedSha256.
            progress?.Report(($"Verifying {tool.Name}...", 93));
            var actualHash = await ComputeSha256Async(downloadPath, ct);

            if (expectedHash is not null)
            {
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Delete the artifact so a tampered/corrupt file is never left behind to be
                    // reused or retried, and fail the install naming the tool and both hashes.
                    try { File.Delete(downloadPath); } catch { /* best effort */ }
                    _logger.LogError(
                        "Integrity check FAILED for {ToolName}: expected SHA-256 {Expected} but the " +
                        "download hashed to {Actual}. The file was deleted and NOT installed.",
                        tool.Name, expectedHash, actualHash);
                    progress?.Report((
                        $"Integrity check failed for {tool.Name}: expected {expectedHash}, got {actualHash}. Not installed.",
                        -1));
                    return null;
                }
                _logger.LogInformation("Integrity check passed for {ToolId} (SHA-256 {Hash})", tool.Id, actualHash);
            }
            else
            {
                // No pinned hash for this tool/platform. The verification machinery is in place; a
                // maintainer must record the SHA-256 of the pinned artifact (see the DownloadUrls
                // comments in KnownTools). Do NOT proceed silently — make the unverified state loud.
                _logger.LogWarning(
                    "UNVERIFIED DOWNLOAD: {ToolName} has no pinned SHA-256 for {Platform}; installing " +
                    "without an integrity check. Downloaded SHA-256 is {Hash} — record it in " +
                    "ToolDefinition.ExpectedSha256 to enable verification. Only install on a trusted network.",
                    tool.Name, platformKey, actualHash);
                progress?.Report(($"Warning: {tool.Name} download is unverified (no pinned checksum).", 94));
            }

            // Extract
            progress?.Report(($"Extracting {tool.Name}...", 95));
            var extractDir = Path.Combine(ToolsDir, tool.Id);
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(downloadPath, extractDir);
            }
            else if (downloadPath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            {
                SevenZipExtractor.ExtractToDirectory(downloadPath, extractDir);
            }
            else if (downloadPath.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)
                  || downloadPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(extractDir);
                using var tarProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = "tar",
                    // No --strip-components: FindBinaryInDirectory searches recursively, so a nested
                    // archive (ffmpeg's <build>/bin/ffmpeg) and a flat one (dovi_tool's bare binary
                    // at the root) both resolve. Stripping the top level would delete the binary from
                    // a flat archive, so it must not be applied blindly.
                    Arguments = $"xf \"{downloadPath}\" -C \"{extractDir}\"",
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
                using var chmod = Process.Start("chmod", $"+x \"{foundBinary}\"");
                chmod?.WaitForExit(5000);
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

    /// <summary>
    /// Computes the SHA-256 of a file as an uppercase hex string. Comparisons against a configured
    /// <see cref="ToolDefinition.ExpectedSha256"/> are case-insensitive, so the casing here is cosmetic.
    /// </summary>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
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

            // Read both pipes concurrently — draining stdout fully before touching stderr can
            // stall for the full 10s timeout when a tool fills the stderr buffer first (e.g.
            // realesrgan/rife emit their help text to stderr), and be misreported as unavailable.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

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
        firstLine = firstLine.Trim();

        // Tools with no version flag (Real-ESRGAN, RIFE) are probed with -h, whose first line is
        // usage text or an option ("Usage: realesrgan-ncnn-vulkan -i …", "-x  enable spatial tta
        // mode") — which the Settings tab then showed as the "version".
        if (firstLine.StartsWith('-') || firstLine.StartsWith("usage", StringComparison.OrdinalIgnoreCase))
            return "installed";

        return firstLine.Length > 80 ? firstLine[..80] : firstLine;
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
