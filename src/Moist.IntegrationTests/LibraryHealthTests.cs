using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Library health checking, against files that are genuinely broken rather than mocked.
///
/// The distinction the tests are here to hold is the one that makes the feature worth
/// having: a file damaged in the middle has a perfectly good header and reports a
/// plausible duration, so a header scan calls it healthy. Only decoding finds it.
/// </summary>
[Collection("media")]
public sealed class LibraryHealthTests(MediaFixture media) : PipelineTestBase(media), IDisposable
{
    private readonly string _library = Path.Combine(media.Root, "library-" + Guid.NewGuid().ToString("N")[..6]);

    public void Dispose()
    {
        try { Directory.Delete(_library, recursive: true); } catch { /* best effort */ }
    }

    private LibraryHealthChecker Checker(AppServices services)
        => new(services.MediaAnalyzer, services.Settings.FFmpegPath);

    private string Library()
    {
        Directory.CreateDirectory(_library);
        return _library;
    }

    private string Copy(string source, string name)
    {
        var path = Path.Combine(Library(), name);
        File.Copy(source, path, overwrite: true);
        return path;
    }

    /// <summary>A file that is not media at all.</summary>
    private string Garbage(string name)
    {
        var path = Path.Combine(Library(), name);
        File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());
        return path;
    }

    /// <summary>
    /// A file whose header is intact but whose middle has been overwritten. This is what
    /// bit rot and a bad drive actually look like, and it is invisible to a header scan.
    /// </summary>
    private string CorruptedInside(string source, string name)
    {
        var path = Copy(source, name);
        var bytes = File.ReadAllBytes(path);
        var random = new Random(1234);

        for (var i = (int)(bytes.Length * 0.35); i < (int)(bytes.Length * 0.6); i++)
            bytes[i] = (byte)random.Next(256);

        File.WriteAllBytes(path, bytes);
        return path;
    }

    [FfmpegFact]
    public async Task AHealthyLibraryReportsNoFaults()
    {
        using var services = CreateServices();
        Copy(Media.SampleVideo, "good.mp4");
        Copy(Media.SceneVideo, "also_good.mp4");

        var report = await Checker(services).ScanAsync(Library(), ct: CancellationToken.None);

        Assert.Equal(2, report.FilesScanned);
        Assert.Empty(report.Faults);
        Assert.Equal(2, report.FilesHealthy);
    }

    [FfmpegFact]
    public async Task AFileThatIsNotMediaIsReported()
    {
        using var services = CreateServices();
        Garbage("junk.mkv");

        var report = await Checker(services).ScanAsync(Library(), ct: CancellationToken.None);

        var issue = Assert.Single(report.Faults);
        Assert.Equal(LibraryIssueKind.Unreadable, issue.Kind);
    }

    [FfmpegFact]
    public async Task DamageInsideAFileIsInvisibleToAHeaderScan()
    {
        // This is the whole reason --deep exists. If this test ever starts failing
        // because the quick scan caught it, the deep scan's cost is no longer justified
        // and this should be revisited rather than "fixed".
        using var services = CreateServices();
        CorruptedInside(Media.NaturalVideo, "rotted.mkv");

        var quick = await Checker(services).ScanAsync(Library(), deep: false, ct: CancellationToken.None);

        Assert.Empty(quick.Faults);
    }

    [FfmpegFact]
    public async Task DamageInsideAFileIsFoundByADeepScan()
    {
        using var services = CreateServices();
        CorruptedInside(Media.NaturalVideo, "rotted.mkv");

        var deep = await Checker(services).ScanAsync(Library(), deep: true, ct: CancellationToken.None);

        Assert.Contains(deep.Faults, i => i.Kind is LibraryIssueKind.DecodeErrors or LibraryIssueKind.Truncated);
    }

    [FfmpegFact]
    public async Task ADeepScanDoesNotAccuseHealthyFiles()
    {
        // A check that cries wolf over a good library is worse than no check.
        using var services = CreateServices();
        Copy(Media.SampleVideo, "good.mp4");
        Copy(Media.NaturalVideo, "also_good.mp4");

        var report = await Checker(services).ScanAsync(Library(), deep: true, ct: CancellationToken.None);

        Assert.Empty(report.Faults);
        Assert.True(report.DeepScan);
    }

    [FfmpegFact]
    public async Task ASilentFileIsNotedButNotCalledBroken()
    {
        // Plenty of legitimate video has no audio; saying so is useful, failing on it
        // is not.
        using var services = CreateServices();
        Copy(Media.NaturalVideo, "silent.mp4");

        var report = await Checker(services).ScanAsync(Library(), ct: CancellationToken.None);

        Assert.Contains(report.Issues, i => i.Kind == LibraryIssueKind.NoAudioStream);
        Assert.Empty(report.Faults);
        Assert.Equal(1, report.FilesHealthy);
    }

    [FfmpegFact]
    public async Task OneBrokenFileDoesNotStopTheScan()
    {
        using var services = CreateServices();
        Garbage("bad.mkv");
        Copy(Media.SampleVideo, "good.mp4");

        var report = await Checker(services).ScanAsync(Library(), ct: CancellationToken.None);

        Assert.Equal(2, report.FilesScanned);
        Assert.Equal(1, report.FilesHealthy);
        Assert.Single(report.Faults);
    }

    [FfmpegFact]
    public async Task SubdirectoriesAreScannedOnlyWhenAsked()
    {
        using var services = CreateServices();
        Copy(Media.SampleVideo, "top.mp4");

        var nested = Path.Combine(Library(), "season 1");
        Directory.CreateDirectory(nested);
        File.Copy(Media.SampleVideo, Path.Combine(nested, "episode.mp4"), overwrite: true);

        var flat = await Checker(services).ScanAsync(Library(), recursive: false, ct: CancellationToken.None);
        var deep = await Checker(services).ScanAsync(Library(), recursive: true, ct: CancellationToken.None);

        Assert.Equal(1, flat.FilesScanned);
        Assert.Equal(2, deep.FilesScanned);
    }

    [FfmpegFact]
    public async Task ProgressIsReportedPerFile()
    {
        using var services = CreateServices();
        Copy(Media.SampleVideo, "a.mp4");
        Copy(Media.SampleVideo, "b.mp4");

        var seen = new List<int>();
        var progress = new Progress<(int Done, int Total, string File)>(p => seen.Add(p.Total));

        await Checker(services).ScanAsync(Library(), progress: progress, ct: CancellationToken.None);

        // Progress is posted asynchronously, so allow it to drain before asserting.
        await Task.Delay(200);
        Assert.All(seen, total => Assert.Equal(2, total));
    }
}
