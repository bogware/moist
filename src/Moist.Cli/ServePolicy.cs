using System.Text.Json;
using Moist.Core.Settings;

namespace Moist.Cli;

/// <summary>
/// What a remote caller of <c>moist serve</c> is allowed to ask for. The API reuses the CLI's
/// option parser so the two can't drift — but the CLI's options were designed for someone at
/// their own keyboard, and several of them are code execution when handed to a stranger:
/// <list type="bullet">
/// <item><c>--ffmpeg</c> / <c>--ffprobe</c> name the binary to run.</item>
/// <item><c>--video-args</c> / <c>--audio-args</c> are appended raw into a <c>cmd</c>/<c>sh</c>
/// pipeline for the standalone encoders.</item>
/// <item><c>--temp-dir</c>, <c>--output-dir</c>, <c>--suffix</c> steer where files are written.</item>
/// <item><c>--filter-engine</c> switches to generated Python (VapourSynth).</item>
/// </list>
/// So the API takes an <b>allowlist</b>: options that only shape the encode. Anything else —
/// including options added to the CLI later — is refused until someone decides it is safe.
/// Paths (input, output, external subtitle, probe) must resolve under a configured
/// <c>--root</c>, and never into moist's own configuration folder.
/// </summary>
public sealed class ServePolicy
{
    /// <summary>Options a remote caller may set. Keep this list conservative.</summary>
    public static readonly IReadOnlySet<string> AllowedOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "profile", "video-encoder", "mode", "crf", "quality", "bitrate", "preset", "tune", "bit-depth", "chroma",
        "container", "fps", "fps-mode", "threads", "filter",
        "av1-film-grain", "av1-keyint", "av1-preset", "av1-tile-columns", "av1-tile-rows", "av1-tune",
        "x265-wrap",
        "audio-encoder", "audio-bitrate", "audio-only", "audio-track", "audio-tracks", "aac-profile",
        "opus-complexity", "flac-level", "mp3-vbr", "vorbis-quality", "sample-rate",
        "normalize-target", "true-peak", "dialnorm",
        "no-subtitles", "subtitle", "subtitle-track", "burn-subtitles", "burn-subtitle-track",
        "no-chapters", "no-attachments", "no-metadata", "preserve-dynamic-hdr",
        "chunked", "chunk-mode", "min-chunk", "max-chunk", "scene-threshold", "no-resume", "no-verify",
        "per-scene", "per-scene-full", "calibration-scenes",
        "target-quality", "metric", "samples", "sample-duration", "max-probes", "measure",
        "ai-upscale", "ai-upscale-height",
        "overwrite",
    };

    /// <summary>Why the most dangerous refusals happen, so the error is actionable rather than a bare "no".</summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ffmpeg"] = "it names the program the server executes",
        ["ffprobe"] = "it names the program the server executes",
        ["video-args"] = "raw encoder arguments are passed through a shell",
        ["audio-args"] = "raw encoder arguments are passed through a shell",
        ["temp-dir"] = "it chooses where the server writes files",
        ["output-dir"] = "pass \"output\" in the request body instead",
        ["suffix"] = "pass \"output\" in the request body instead",
        ["output"] = "pass \"output\" in the request body instead",
        ["filter-engine"] = "the VapourSynth engine runs generated Python on the server",
    };

    private readonly string[] _roots;
    private readonly string _configRoot;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <param name="roots">Directories requests may touch. Empty = unrestricted (loopback only).</param>
    public ServePolicy(IEnumerable<string>? roots, string? configRoot = null)
    {
        _roots = (roots ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize).Distinct().ToArray();
        _configRoot = Normalize(configRoot ?? MoistPaths.ConfigRoot);
    }

    public IReadOnlyList<string> Roots => _roots;
    public bool Restricted => _roots.Length > 0;

    /// <summary>Null when every option is allowed; otherwise the refusal to send back.</summary>
    public static string? CheckOptions(IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (options is null) return null;
        foreach (var (raw, value) in options)
        {
            var key = raw.TrimStart('-');
            if (!AllowedOptions.Contains(key))
                return Reasons.TryGetValue(key, out var why)
                    ? $"option '{key}' is not accepted over the API: {why}"
                    : $"option '{key}' is not accepted over the API";

            // A switch is true or false, nothing else. A string handed to one was how a forbidden
            // option once rode in ("overwrite": "--video-args=…"); refuse the malformed request.
            if (CommandLine.IsFlag(key) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                return $"option '{key}' is a switch: use true or false";
        }
        return null;
    }

    /// <summary>
    /// The check that matters: what the parser produced from the request, not what the JSON said.
    /// Every parsed option must be allowed and there must be no stray positional arguments —
    /// whatever token sequence a request manages to construct.
    /// </summary>
    public static string? CheckParsed(IEnumerable<string> optionNames, IReadOnlyList<string> positional)
    {
        foreach (var name in optionNames)
            if (!AllowedOptions.Contains(name))
                return Reasons.TryGetValue(name, out var why)
                    ? $"option '{name}' is not accepted over the API: {why}"
                    : $"option '{name}' is not accepted over the API";
        return positional.Count > 0 ? "unexpected positional arguments in options" : null;
    }

    /// <summary>
    /// Resolve <paramref name="path"/> and check it may be touched. Returns the full path, or null
    /// with the reason in <paramref name="error"/>.
    /// </summary>
    public string? Resolve(string path, string what, out string? error)
    {
        error = null;
        string full;
        try { full = RealPath(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            error = $"{what}: invalid path";
            return null;
        }

        // moist's own settings, profiles, plugins and tools: writing there is code execution on
        // the next start, reading there exposes configuration. Never, whatever the roots say.
        if (IsUnder(full, _configRoot))
        {
            error = $"{what}: moist's configuration folder is off limits";
            return null;
        }

        if (Restricted && !_roots.Any(r => IsUnder(full, r)))
        {
            error = $"{what}: outside the served roots";
            return null;
        }
        return full;
    }

    private static bool IsUnder(string full, string root)
        => (full + Path.DirectorySeparatorChar).StartsWith(root, PathComparison);

    private static string Normalize(string dir)
    {
        var full = RealPath(Path.GetFullPath(dir));
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// <paramref name="full"/> with every symbolic link and junction along it resolved. GetFullPath
    /// only tidies the text, so a link inside a served root ("/media/x" → "/etc") passed the prefix
    /// check while the file operation landed outside the root. The part of the path that doesn't
    /// exist yet (a new output file) is appended unchanged to its resolved, existing parent.
    /// </summary>
    internal static string RealPath(string full, int depth = 0)
    {
        if (depth > 32) throw new IOException("too many levels of symbolic links");
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next)
                                 : File.Exists(next) ? new FileInfo(next)
                                 : null;
            if (info is null)
                return Path.Combine([current, .. parts[i..]]);   // the rest doesn't exist yet
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                next = RealPath(Path.GetFullPath(target.FullName), depth + 1);   // the target may sit behind links too
            current = next;
        }
        return current;
    }
}
