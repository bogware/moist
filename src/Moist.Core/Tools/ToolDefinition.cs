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
    /// The tool's official download / information page, opened in the user's browser when there is
    /// no in-app auto-download (i.e. <see cref="DownloadUrls"/> is empty). Used for tools that
    /// cannot be dropped in as a single binary — an installer-based suite, a Python ecosystem, or a
    /// build the user must obtain themselves — so the prerequisites panel can still offer a one-click
    /// "Get" that points them straight at the right page rather than leaving them stranded.
    /// </summary>
    public string? HelpUrl { get; init; }

    /// <summary>
    /// Expected SHA-256 of each platform's download artifact, keyed by the same platform key as
    /// <see cref="DownloadUrls"/> ("win-x64", "linux-x64", "osx-x64", "osx-arm64"). Value is a hex
    /// string; comparison is case-insensitive.
    /// <para>
    /// When an entry is present, the downloaded file is verified against it before extraction and a
    /// mismatch fails the install. When an entry is absent, the install proceeds but is flagged as an
    /// unverified download (a loud warning), never silently. Only meaningful for a URL pinned to an
    /// immutable (dated/versioned) release — a rolling "latest" tag serves changing bytes and so
    /// cannot be pinned to a fixed hash.
    /// </para>
    /// </summary>
    public Dictionary<string, string>? ExpectedSha256 { get; init; }

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
