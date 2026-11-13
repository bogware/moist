using System.Text.Json;
using Moist.Cli;
using Moist.Core.Jobs;
using Moist.Filters;
using Moist.Filters.Engines;

namespace Moist.Core.Tests;

/// <summary>
/// What a remote <c>moist serve</c> caller can reach. The API reuses the CLI parser, whose options
/// were designed for the person at the keyboard — several of them are code execution in a
/// stranger's hands. These pin the allowlist, the path confinement and the injection guards.
/// </summary>
public class ServePolicyTests
{
    private static Dictionary<string, JsonElement> Options(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Theory]
    [InlineData("ffmpeg")]          // names the binary the server runs
    [InlineData("ffprobe")]
    [InlineData("video-args")]      // raw text into cmd/sh
    [InlineData("audio-args")]
    [InlineData("temp-dir")]
    [InlineData("output-dir")]
    [InlineData("suffix")]
    [InlineData("filter-engine")]   // generated Python
    [InlineData("some-future-option")]
    public void DangerousOrUnknownOptions_AreRefused(string key)
    {
        var error = ServePolicy.CheckOptions(Options($$"""{"{{key}}": "x"}"""));
        Assert.NotNull(error);
        Assert.Contains(key, error);
    }

    [Fact]
    public void EncodeShapingOptions_AreAllowed()
        => Assert.Null(ServePolicy.CheckOptions(Options("""{"crf": 22, "preset": "slow", "filter": ["crop:left=8"], "chunked": true, "x265-wrap": "auto"}""")));

    [Fact]
    public void LeadingDashes_DoNotSmuggleAnOptionPast()
        => Assert.NotNull(ServePolicy.CheckOptions(Options("""{"--ffmpeg": "/bin/sh"}""")));

    private static string Dir(params string[] parts)
        => Path.Combine([Path.GetTempPath(), "moist_policy", .. parts]);

    [Fact]
    public void Paths_MustResolveUnderARoot()
    {
        var policy = new ServePolicy([Dir("media")], configRoot: Dir("config"));
        Assert.Equal(Path.GetFullPath(Dir("media", "a.mkv")), policy.Resolve(Dir("media", "a.mkv"), "input", out _));
        Assert.Null(policy.Resolve(Dir("elsewhere", "a.mkv"), "input", out var error));
        Assert.Contains("outside", error);
    }

    [Fact]
    public void DotDot_CannotClimbOutOfARoot()
        => Assert.Null(new ServePolicy([Dir("media")], Dir("config")).Resolve(Dir("media", "..", "secret.txt"), "input", out _));

    [Fact]
    public void ASiblingWithTheSamePrefix_IsNotUnderTheRoot()
        // "/srv/media-private" starts with "/srv/media" as a string; it is not inside it.
        => Assert.Null(new ServePolicy([Dir("media")], Dir("config")).Resolve(Dir("media-private", "a.mkv"), "input", out _));

    [Fact]
    public void TheConfigFolder_IsRefusedEvenWhenUnrestrictedOrInsideARoot()
    {
        // Writing settings.json or a plugin DLL there is code execution on the next start.
        Assert.Null(new ServePolicy(null, Dir("config")).Resolve(Dir("config", "plugins", "x.dll"), "output", out _));
        Assert.Null(new ServePolicy([Dir()], Dir("config")).Resolve(Dir("config", "settings.json"), "output", out var error));
        Assert.Contains("configuration", error);
    }

    [Fact]
    public void Unrestricted_AllowsAnythingButConfig()
        => Assert.NotNull(new ServePolicy(null, Dir("config")).Resolve(Dir("anywhere", "a.mkv"), "input", out _));

    [Fact]
    public void FreeTextFilterValues_CannotOpenASecondFiltergraph()
    {
        var setpts = new FFmpegFilterEngine().GetAvailableFilters().First(f => f.Parameters.Values.Any(p => p.Type == FilterParameterType.String));
        var key = setpts.Parameters.First(p => p.Value.Type == FilterParameterType.String).Key;
        AppliedFilter With(string v) => new() { Filter = setpts, ParameterValues = new() { [key] = v } };

        Assert.Empty(With("PTS-STARTPTS").Validate());
        Assert.NotEmpty(With("PTS;movie=/etc/passwd").Validate());
        Assert.NotEmpty(With("PTS[x]").Validate());
        Assert.NotEmpty(With("PTS\"").Validate());
        Assert.NotEmpty(With("PTS\nimport os").Validate());
    }

    [Fact]
    public void VapourSynthKernel_IsMapped_NotInterpolatedIntoPython()
    {
        var engine = new VapourSynthFilterEngine();
        var resize = engine.GetAvailableFilters().First(f => f.Id == "vs_resize");
        var chain = new FilterChain();
        chain.Add(resize, new() { ["kernel"] = "Lanczos(clip)\nimport os; os.system('id')#" });
        var script = engine.GenerateScript(chain, "in.mkv");
        Assert.DoesNotContain("os.system", script);
        Assert.Contains("core.resize.Lanczos(", script);
    }

    [Fact]
    public void WindowsShellEscape_RefusesQuotesAndNewlines()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => ShellPipeline.Escape("a\" & calc & \""));
        Assert.Throws<ArgumentException>(() => ShellPipeline.Escape("a\r\nb"));
        Assert.Equal("\"C:\\some dir\\in.mkv\"", ShellPipeline.Escape(@"C:\some dir\in.mkv"));
    }
}
