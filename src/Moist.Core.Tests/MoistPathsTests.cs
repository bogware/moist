using Moist.Core.Settings;

namespace Moist.Core.Tests;

/// <summary>
/// The config root must always be absolute. GetFolderPath(ApplicationData) returns "" on a Linux
/// account without ~/.config, and the old code turned that into the relative path "moist" —
/// which, beside the published CLI, is the executable itself.
/// </summary>
public class MoistPathsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\x" : "/x";
    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);
    private static readonly string Temp = Path.GetTempPath();

    [Fact]
    public void Override_WinsAndIsUsedAsIs()
        => Assert.Equal(P("cfg"), MoistPaths.Resolve(P("cfg"), P("appdata"), null, null, null, Temp));

    [Fact]
    public void AppData_IsUsedWhenPresent()
        => Assert.Equal(P("appdata", "moist"), MoistPaths.Resolve(null, P("appdata"), P("xdg"), P("home"), null, Temp));

    [Fact]
    public void EmptyAppData_FallsBackToXdgConfigHome()
        => Assert.Equal(P("xdg", "moist"), MoistPaths.Resolve(null, "", P("xdg"), P("home"), null, Temp));

    [Fact]
    public void EmptyAppDataAndXdg_FallsBackToHomeDotConfig()
        => Assert.Equal(P("home", ".config", "moist"), MoistPaths.Resolve(null, "", "", P("home"), null, Temp));

    [Fact]
    public void RelativeCandidates_AreNeverUsed()
    {
        // A relative override or XDG value would put config wherever the process happened to start.
        var r = MoistPaths.Resolve("rel", "moist", "rel2", "rel3", null, Temp);
        Assert.True(Path.IsPathRooted(r));
        Assert.Equal(Path.Combine(Path.GetFullPath(Temp), "moist-config"), r);
    }

    [Fact]
    public void LiveConfigRoot_IsAbsolute()
        => Assert.True(Path.IsPathRooted(MoistPaths.ConfigRoot), MoistPaths.ConfigRoot);
}
