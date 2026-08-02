using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Chunking;
using Moist.Core.Media;

namespace Moist.Services;

/// <summary>What a chunk plan turned into on disk, including anything reusable.</summary>
public sealed class ChunkedEncodePreparation
{
    public required ChunkPlan Plan { get; init; }
    public required string ChunkDirectory { get; init; }

    /// <summary>Output path for each chunk, indexed the same as <see cref="ChunkPlan.Chunks"/>.</summary>
    public required IReadOnlyList<string> ChunkFiles { get; init; }

    /// <summary>Chunks already encoded and verified from an earlier run.</summary>
    public required IReadOnlySet<int> AlreadyDone { get; init; }

    /// <summary>
    /// Per-chunk quality override, set by per-scene optimisation. Chunks absent from the
    /// map use the request's quality.
    /// </summary>
    public IReadOnlyDictionary<int, double> PerChunkQuality { get; init; }
        = new Dictionary<int, double>();

    public int RemainingCount => Plan.Chunks.Count - AlreadyDone.Count;

}

/// <summary>
/// Works out how to split an encode into chunks, and what of a previous attempt can be
/// reused.
///
/// Splitting only pays off past a certain length: below that the forced keyframe at each
/// boundary and the scheduling overhead cost more than the parallelism gains, which is
/// why <see cref="PrepareAsync"/> can decline.
/// </summary>
public sealed class ChunkedEncode(AppServices services, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<ChunkedEncode>() ?? NullLogger<ChunkedEncode>.Instance;

    /// <summary>Progress during the scene-detection pass, 0-1.</summary>
    public event Action<double>? DetectionProgress;

    /// <summary>
    /// Plan and prepare in one step. Returns null when chunking isn't worthwhile.
    /// </summary>
    public async Task<ChunkedEncodePreparation?> PrepareAsync(
        EncodeRequest request, MediaFileInfo info, ChunkingOptions options,
        IReadOnlyDictionary<int, double>? perChunkQuality = null, CancellationToken ct = default)
    {
        var plan = await PlanAsync(request, info, options, ct);
        return plan is null ? null : Prepare(request, plan, options, perChunkQuality);
    }

    /// <summary>
    /// Work out the chunk boundaries. Separate from <see cref="Prepare"/> because
    /// per-scene optimisation needs the plan before it can decide each chunk's quality,
    /// and that quality has to be known before the chunk directory is named.
    ///
    /// Returns null when chunking isn't worthwhile — the caller should encode normally.
    /// </summary>
    public async Task<ChunkPlan?> PlanAsync(
        EncodeRequest request, MediaFileInfo info, ChunkingOptions options, CancellationToken ct = default)
    {
        var video = info.Video;
        if (video is null || video.Framerate <= 0)
        {
            _logger.LogInformation("No usable video stream metadata; encoding without chunking");
            return null;
        }

        var totalFrames = video.TotalFrames > 0
            ? video.TotalFrames
            : (long)Math.Round(info.Duration.TotalSeconds * video.Framerate);

        if (!ChunkPlanner.IsWorthwhile(totalFrames, video.Framerate, options))
        {
            _logger.LogInformation(
                "Source is too short to be worth chunking ({Duration:0.0}s); encoding normally",
                info.Duration.TotalSeconds);
            return null;
        }

        IReadOnlyList<long> sceneCuts = [];
        if (options.Mode == ChunkMode.Scene)
        {
            var detector = new SceneDetector(services.Settings.FFmpegPath, loggerFactory);
            detector.Progress += p => DetectionProgress?.Invoke(p);

            sceneCuts = await detector.DetectAsync(
                request.InputFile, video.Framerate, options.SceneThreshold, info.Duration, ct);

            if (sceneCuts.Count == 0)
                _logger.LogInformation("No scene changes found; falling back to fixed-length chunks");
        }

        var plan = ChunkPlanner.Plan(totalFrames, video.Framerate, options, sceneCuts);

        _logger.LogInformation(
            "Chunk plan: {Chunks} chunk(s) over {Frames} frames ({Mode}, {Cuts} scene cut(s))",
            plan.Chunks.Count, plan.TotalFrames, plan.Mode, sceneCuts.Count);

        return plan;
    }

    /// <summary>
    /// Settle on chunk file paths and work out what an earlier run left behind.
    /// </summary>
    public ChunkedEncodePreparation Prepare(
        EncodeRequest request, ChunkPlan plan, ChunkingOptions options,
        IReadOnlyDictionary<int, double>? perChunkQuality = null)
    {
        // The directory name covers the per-chunk qualities too, so a run at a different
        // target can't reuse chunks encoded for the previous one.
        var chunkDir = Path.Combine(
            request.TempDirectory, "chunks", FingerprintFor(request, plan, options, perChunkQuality));
        Directory.CreateDirectory(chunkDir);

        var files = plan.Chunks
            .Select(c => Path.Combine(chunkDir, $"chunk_{c.Index:D5}.mkv"))
            .ToList();

        var done = options.Resume
            ? FindReusableAsync(plan, files, CancellationToken.None).GetAwaiter().GetResult()
            : new HashSet<int>();

        if (done.Count > 0)
        {
            _logger.LogInformation("Reusing {Done} of {Total} chunks from a previous run",
                done.Count, plan.Chunks.Count);
        }

        return new ChunkedEncodePreparation
        {
            Plan = plan,
            ChunkDirectory = chunkDir,
            ChunkFiles = files,
            AlreadyDone = done,
            PerChunkQuality = perChunkQuality ?? new Dictionary<int, double>(),
        };
    }

    /// <summary>
    /// Which chunks survive from a previous attempt. A chunk counts only if it decodes to
    /// exactly the frames the plan expects — a half-written file left by a kill would
    /// otherwise be spliced straight into the result.
    /// </summary>
    private async Task<HashSet<int>> FindReusableAsync(
        ChunkPlan plan, IReadOnlyList<string> files, CancellationToken ct)
    {
        var done = new HashSet<int>();

        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(files[i])) continue;

            try
            {
                var frames = await FFmpegConcatProcessor.CountFramesAsync(
                    services.Settings.FFprobePath, files[i], ct);

                if (frames == plan.Chunks[i].FrameCount)
                {
                    done.Add(i);
                }
                else
                {
                    _logger.LogDebug("Discarding chunk {Index}: has {Actual} frames, expected {Expected}",
                        i, frames, plan.Chunks[i].FrameCount);
                    TryDelete(files[i]);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                TryDelete(files[i]);
            }
        }

        return done;
    }

    /// <summary>
    /// Identifies a source and the settings that shape its chunks. Anything that would
    /// change a chunk's contents has to be in here, or a resumed run splices together
    /// chunks encoded with different settings.
    /// </summary>
    internal static string FingerprintFor(
        EncodeRequest request, ChunkPlan plan, ChunkingOptions options,
        IReadOnlyDictionary<int, double>? perChunkQuality = null)
    {
        var identity = new StringBuilder();
        identity.Append(Path.GetFullPath(request.InputFile).ToLowerInvariant()).Append('|');

        try
        {
            var file = new FileInfo(request.InputFile);
            if (file.Exists)
                identity.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|');
        }
        catch
        {
            // A weaker fingerprint is fine; it just means fewer resume hits.
        }

        identity.Append(request.VideoEncoderId).Append('|')
                .Append(request.Mode).Append('|')
                .Append(request.Quality.ToString("F3")).Append('|')
                .Append(request.Bitrate).Append('|')
                .Append(request.Preset).Append('|')
                .Append(request.Tune).Append('|')
                .Append(request.SvtAv1Preset).Append('|')
                .Append(request.BitDepth).Append('|')
                .Append(request.VideoCustomArgs).Append('|')
                .Append(request.FilterScript ?? "").Append('|')
                .Append(options.Mode).Append('|')
                // The boundaries themselves matter: same settings, different split,
                // different chunks.
                .Append(plan.Chunks.Count).Append('|')
                .Append(string.Join(",", plan.Chunks.Select(c => c.StartFrame)));

        // Per-scene assignments change what each chunk contains, so they belong here too.
        if (perChunkQuality is { Count: > 0 })
        {
            identity.Append('|').Append(string.Join(",",
                perChunkQuality.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value:F1}")));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete stale chunk {Path}", path); }
    }
}
