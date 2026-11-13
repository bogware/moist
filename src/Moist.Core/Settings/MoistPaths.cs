namespace Moist.Core.Settings;

/// <summary>
/// The one place that decides where moist keeps its configuration (settings, profiles, logs,
/// tools, plugins, queue, caches). Every caller goes through <see cref="ConfigRoot"/> or
/// <see cref="Sub"/> rather than calling <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// itself.
/// </summary>
/// <remarks>
/// <para>This exists because <c>GetFolderPath(ApplicationData)</c> returns an <b>empty string</b>
/// on Linux/macOS when <c>~/.config</c> doesn't exist yet — a fresh account, a CI runner, a
/// service user. <c>Path.Combine("", "moist")</c> is then the <i>relative</i> path <c>moist</c>,
/// so the config silently landed in whatever the working directory was. Next to the published
/// CLI that path is the <c>moist</c> executable itself, and every write failed with
/// "The file '…/moist' already exists".</para>
/// <para>Resolution order: <c>MOIST_CONFIG_DIR</c> (explicit override) → the platform's
/// application-data folder, created if missing → <c>$XDG_CONFIG_HOME</c> → <c>$HOME/.config</c>
/// → local application data → the temp directory. The result is always absolute.</para>
/// </remarks>
public static class MoistPaths
{
    /// <summary>Environment variable that overrides the configuration root.</summary>
    public const string OverrideVariable = "MOIST_CONFIG_DIR";

    /// <summary>The absolute configuration root (…/moist). Not created — use <see cref="Sub"/> or
    /// <see cref="EnsureRoot"/> when you need it to exist.</summary>
    public static string ConfigRoot => Resolve(
        Environment.GetEnvironmentVariable(OverrideVariable),
        TryFolder(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("HOME"),
        TryFolder(Environment.SpecialFolder.LocalApplicationData),
        Path.GetTempPath());

    /// <summary>The configuration root, created if needed.</summary>
    public static string EnsureRoot()
    {
        var root = ConfigRoot;
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A sub-directory of the configuration root (e.g. "logs", "profiles"), created if needed.</summary>
    public static string Sub(string name)
    {
        var dir = Path.Combine(ConfigRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Pure resolution logic, separated so it can be unit-tested without touching the
    /// process environment. Each candidate is used only if it is a non-empty absolute path.</summary>
    internal static string Resolve(string? overrideDir, string? appData, string? xdgConfigHome,
                                   string? home, string? localAppData, string tempPath)
    {
        if (Usable(overrideDir)) return Path.GetFullPath(overrideDir!);            // already the moist dir
        if (Usable(appData)) return Path.Combine(appData!, "moist");
        if (Usable(xdgConfigHome)) return Path.Combine(xdgConfigHome!, "moist");
        if (Usable(home)) return Path.Combine(home!, ".config", "moist");
        if (Usable(localAppData)) return Path.Combine(localAppData!, "moist");
        return Path.Combine(Path.GetFullPath(tempPath), "moist-config");
    }

    private static bool Usable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    /// <summary>GetFolderPath with <see cref="Environment.SpecialFolderOption.Create"/>, so a
    /// missing ~/.config is created rather than reported as "". Never throws.</summary>
    private static string? TryFolder(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create); }
        catch { return null; }
    }
}
