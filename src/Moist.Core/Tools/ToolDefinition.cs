namespace Moist.Core.Tools;

/// <summary>
/// Defines an external tool/binary that moist depends on.
/// </summary>
public sealed class ToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Default binary name (e.g. "ffmpeg", "ffprobe"). Used for PATH lookup.</summary>
    public required string DefaultBinaryName { get; init; }

    /// <summary>Whether this tool is strictly required for the app to function.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Arguments to pass to verify the tool works (e.g. "-version").</summary>
    public string VersionArgs { get; init; } = "-version";

    /// <summary>Download URLs per platform. Key = "win-x64", "linux-x64", "osx-x64", "osx-arm64".</summary>
    public Dictionary<string, string> DownloadUrls { get; init; } = new();

    /// <summary>
    /// If true, the download is an archive (zip/tar.gz) and needs extraction.
    /// The binary name will be searched inside the extracted folder.
    /// </summary>
    public bool IsArchiveDownload { get; init; } = true;
}

/// <summary>
/// Result of checking a tool's availability.
/// </summary>
public sealed class ToolStatus
{
    public required ToolDefinition Tool { get; init; }
    public bool IsAvailable { get; set; }
    public string? DetectedPath { get; set; }
    public string? Version { get; set; }
    public string? Error { get; set; }
}
