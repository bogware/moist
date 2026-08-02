using Moist.Services;

namespace Moist.Core.Tests;

/// <summary>
/// The watch folder.
///
/// Nothing here needs FFmpeg: readiness is decided by comparing two sightings of a file,
/// so the timing can be driven exactly rather than waited out.
/// </summary>
public sealed class WatchFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "moist-watch-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _watched;
    private readonly string _output;
    private readonly string _state;

    public WatchFolderTests()
    {
        _watched = Path.Combine(_root, "in");
        _output = Path.Combine(_root, "out");
        _state = Path.Combine(_root, "state.json");
        Directory.CreateDirectory(_watched);
        Directory.CreateDirectory(_output);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private WatchFolderOptions Options(Action<WatchFolderOptionsBuilder>? tweak = null)
    {
        var builder = new WatchFolderOptionsBuilder { Path = _watched, OutputDirectory = _output };
        tweak?.Invoke(builder);
        return builder.Build();
    }

    private WatchFolderService Service(WatchFolderOptions? options = null)
        => new(options ?? Options(), loggerFactory: null, statePath: _state);

    private string Drop(string name, int bytes = 1024)
    {
        var path = Path.Combine(_watched, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void AFileIsNotEncodedTheFirstTimeItIsSeen()
    {
        // A file arriving in a watch folder is usually still being copied. It has a valid
        // header and a plausible duration the whole way through, so encoding one early
        // yields a truncated result and no error anywhere to explain it.
        var service = Service();
        Drop("arriving.mkv");

        Assert.Empty(service.Ready());
    }

    [Fact]
    public void AFileIsEncodedOnceItHasStoppedChanging()
    {
        var service = Service();
        var file = Drop("settled.mkv");

        service.Ready();

        Assert.Equal([file], service.Ready());
    }

    [Fact]
    public void AFileThatIsStillGrowingKeepsWaiting()
    {
        var service = Service();
        var file = Drop("growing.mkv", 1024);

        service.Ready();

        // Still being written: the clock has to start again, not carry on.
        File.WriteAllBytes(file, new byte[4096]);
        Assert.Empty(service.Ready());

        Assert.Equal([file], service.Ready());
    }

    [Fact]
    public void EmptyFilesAreIgnored()
    {
        // Some copy tools create the file at full size, others create it empty first.
        var service = Service();
        Drop("placeholder.mkv", bytes: 0);

        service.Ready();
        Assert.Empty(service.Ready());
    }

    [Fact]
    public void FilesOfOtherKindsAreIgnored()
    {
        var service = Service();
        File.WriteAllText(Path.Combine(_watched, "notes.txt"), "not a video");
        File.WriteAllText(Path.Combine(_watched, "cover.jpg"), "not a video");

        service.Ready();
        Assert.Empty(service.Ready());
    }

    [Fact]
    public async Task AFileIsNotEncodedTwice()
    {
        var service = Service();
        Drop("once.mkv");
        service.Ready();

        var encoded = new List<string>();
        await RunOnceAsync(service, file => { encoded.Add(file); return true; });

        Assert.Single(encoded);

        service.Ready();
        Assert.Empty(service.Ready());
    }

    [Fact]
    public async Task AFileThatChangesAfterwardsIsEncodedAgain()
    {
        // Replacing a source with a better rip is a real workflow, and the watcher
        // having "seen that name already" is the wrong reason to skip it.
        var service = Service();
        var file = Drop("replaced.mkv");
        service.Ready();
        await RunOnceAsync(service, _ => true);

        File.WriteAllBytes(file, new byte[8192]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

        service.Ready();
        Assert.Equal([file], service.Ready());
    }

    [Fact]
    public async Task WhatHasBeenEncodedSurvivesARestart()
    {
        var first = Service();
        Drop("remembered.mkv");
        first.Ready();
        await RunOnceAsync(first, _ => true);

        // A watcher that forgot its history on restart would re-encode the folder.
        var second = Service();
        second.Ready();
        Assert.Empty(second.Ready());
    }

    [Fact]
    public void WritingEncodesIntoTheWatchedFolderIsRefused()
    {
        // Otherwise the watcher encodes its own output, then encodes that, until the
        // disk fills.
        var ex = Assert.Throws<ArgumentException>(() =>
            Service(Options(o => o.OutputDirectory = Path.Combine(_watched, "encoded"))));

        Assert.Contains("re-encoded", ex.Message);
    }

    [Fact]
    public void TheWatchedFolderItselfIsRefusedAsAnOutput()
    {
        Assert.Throws<ArgumentException>(() => Service(Options(o => o.OutputDirectory = _watched)));
    }

    [Fact]
    public void MovingHandledFilesNeedsSomewhereToMoveThem()
    {
        Assert.Throws<ArgumentException>(() =>
            Service(Options(o => o.PostAction = WatchPostAction.Move)));
    }

    [Fact]
    public void MovingIntoARecursivelyWatchedFolderIsRefused()
    {
        // The moved file would be found again on the next scan.
        Assert.Throws<ArgumentException>(() => Service(Options(o =>
        {
            o.Recursive = true;
            o.PostAction = WatchPostAction.Move;
            o.MoveTo = Path.Combine(_watched, "done");
        })));
    }

    [Fact]
    public async Task ASourceIsDeletedOnlyAfterItsEncodeSucceeds()
    {
        var service = Service(Options(o => o.PostAction = WatchPostAction.Delete));
        var file = Drop("disposable.mkv");
        service.Ready();

        await RunOnceAsync(service, _ => true);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task AFailedEncodeLeavesItsSourceAlone()
    {
        // Deleting the only copy of something because the encode failed would be
        // unrecoverable.
        var service = Service(Options(o => o.PostAction = WatchPostAction.Delete));
        var file = Drop("precious.mkv");
        service.Ready();

        await RunOnceAsync(service, _ => false);

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task MovedSourcesDoNotOverwriteEachOther()
    {
        var archive = Path.Combine(_root, "archive");
        Directory.CreateDirectory(archive);
        File.WriteAllBytes(Path.Combine(archive, "clash.mkv"), new byte[16]);

        var service = Service(Options(o =>
        {
            o.PostAction = WatchPostAction.Move;
            o.MoveTo = archive;
        }));

        Drop("clash.mkv", 2048);
        service.Ready();
        await RunOnceAsync(service, _ => true);

        Assert.Equal(2, Directory.GetFiles(archive, "clash*").Length);
        Assert.Equal(16, new FileInfo(Path.Combine(archive, "clash.mkv")).Length);
    }

    [Fact]
    public void FilesAlreadyThereCanBeWrittenOffAtStartup()
    {
        Drop("preexisting.mkv");

        var service = Service(Options(o => o.IncludeExisting = false));

        // RunAsync marks them; a cancelled token stops it after that first pass.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { service.RunAsync((_, _) => Task.FromResult(new WatchResult("", null, true, null)), null, cancelled.Token).Wait(); }
        catch { /* cancellation is the expected end */ }

        service.Ready();
        Assert.Empty(service.Ready());
    }

    [Fact]
    public void SubdirectoriesAreOnlySearchedWhenAsked()
    {
        var nested = Path.Combine(_watched, "season 1");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "episode.mkv"), new byte[1024]);

        var flat = Service();
        flat.Ready();
        Assert.Empty(flat.Ready());

        var deep = new WatchFolderService(Options(o => o.Recursive = true), null,
            Path.Combine(_root, "state2.json"));
        deep.Ready();
        Assert.Single(deep.Ready());
    }

    /// <summary>Run one pass of the watch loop and stop.</summary>
    private static async Task RunOnceAsync(WatchFolderService service, Func<string, bool> succeeds)
    {
        using var stop = new CancellationTokenSource();
        try
        {
            await service.RunAsync(
                encode: (file, _) =>
                {
                    var ok = succeeds(file);
                    return Task.FromResult(new WatchResult(file, file + ".out", ok, ok ? null : "encode failed"));
                },
                onFinished: _ => stop.Cancel(),
                ct: stop.Token);
        }
        catch (OperationCanceledException) { /* the intended way out */ }
    }
}

/// <summary>Mutable builder so tests can vary one field of the init-only options.</summary>
public sealed class WatchFolderOptionsBuilder
{
    public required string Path { get; init; }
    public string? OutputDirectory { get; set; }
    public bool Recursive { get; set; }
    public WatchPostAction PostAction { get; set; } = WatchPostAction.Keep;
    public string? MoveTo { get; set; }
    public bool IncludeExisting { get; set; } = true;

    public WatchFolderOptions Build() => new()
    {
        Path = Path,
        OutputDirectory = OutputDirectory,
        Recursive = Recursive,
        PostAction = PostAction,
        MoveTo = MoveTo,
        IncludeExisting = IncludeExisting,
        // Readiness is then decided purely by seeing the file twice unchanged, with no
        // waiting involved.
        StableFor = TimeSpan.Zero,
        ScanInterval = TimeSpan.FromMilliseconds(1),
    };
}
