using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Chunking;
using Moist.Core.Encoders.Video;
using Moist.Core.Media;
using Moist.Core.Quality;

namespace Moist.Services;

public enum PerSceneStrategy
{
    /// <summary>
    /// Search a few representative scenes properly, then predict the rest from a
    /// complexity model. Costs a handful of searches instead of one per scene.
    /// </summary>
    Modeled,

    /// <summary>
    /// Search every scene independently. The most accurate answer, and by far the most
    /// expensive — a full CRF search per chunk.
    /// </summary>
    Full,
}

public sealed record PerSceneOptions
{
    public QualityMetric Metric { get; init; } = QualityMetric.Vmaf;
    public double? Target { get; init; }
    public PerSceneStrategy Strategy { get; init; } = PerSceneStrategy.Modeled;

    /// <summary>How many scenes to search properly before modelling the rest.</summary>
    public int CalibrationScenes { get; init; } = 3;

    /// <summary>Frames of each scene to encode when measuring complexity.</summary>
    public int ComplexityProbeFrames { get; init; } = 50;

    /// <summary>
    /// Check each modelled scene at its predicted CRF and search properly when it misses.
    ///
    /// This is not paranoia. Complexity is measured as compressibility, and the two come
    /// apart: a SMPTE bars pattern compresses to almost nothing yet its razor-sharp edges
    /// are exactly what a perceptual metric punishes when quantised hard. Left unchecked,
    /// the model handed that scene CRF 42 and it scored 80 against a target of 93.
    /// </summary>
    public bool VerifyModelled { get; init; } = true;

    /// <summary>How far below target a modelled scene may land before it's re-searched.</summary>
    public double VerifyTolerance { get; init; } = 1.0;

    /// <summary>Reference CRF for the complexity probe. Only relative values matter.</summary>
    public int ComplexityProbeCrf { get; init; } = 26;

    public double ResolvedTarget => Target ?? Metric.DefaultTarget();
}

/// <summary>What one scene was assigned, and why.</summary>
public sealed class SceneAssignment
{
    public required int ChunkIndex { get; init; }
    public required int Crf { get; init; }

    /// <summary>Encoded bytes per frame at the reference CRF — the complexity signal.</summary>
    public required double Complexity { get; init; }

    /// <summary>True when this scene was searched rather than predicted.</summary>
    public bool Calibrated { get; init; }

    /// <summary>Score measured during calibration or verification.</summary>
    public double? MeasuredScore { get; init; }

    /// <summary>The model's prediction was checked and found wanting, so it was searched.</summary>
    public bool Corrected { get; init; }

    public string Explanation => Corrected ? "corrected"
        : Calibrated ? "searched"
        : "modelled";
}

public sealed class PerSceneResult
{
    public required IReadOnlyList<SceneAssignment> Assignments { get; init; }
    public required QualityMetric Metric { get; init; }
    public required double Target { get; init; }
    public TimeSpan Elapsed { get; init; }
    public PerSceneStrategy Strategy { get; init; }

    public int CalibratedCount => Assignments.Count(a => a.Calibrated);
    public int CorrectedCount => Assignments.Count(a => a.Corrected);
    public int MinCrf => Assignments.Min(a => a.Crf);
    public int MaxCrf => Assignments.Max(a => a.Crf);

    /// <summary>Per-chunk quality, ready to hand to the job builder.</summary>
    public IReadOnlyDictionary<int, double> ChunkQuality
        => Assignments.ToDictionary(a => a.ChunkIndex, a => (double)a.Crf);

    public string Summary
    {
        get
        {
            if (MinCrf == MaxCrf) return $"every scene at CRF {MinCrf}";

            var modelled = Assignments.Count - CalibratedCount;
            var corrected = CorrectedCount > 0 ? $", {CorrectedCount} corrected" : "";
            return $"CRF {MinCrf}-{MaxCrf} across {Assignments.Count} scenes " +
                   $"({CalibratedCount} searched, {modelled} modelled{corrected})";
        }
    }
}

/// <summary>
/// Gives each scene its own CRF so perceptual quality is uniform across the file,
/// rather than bitrate being uniform.
///
/// A fixed CRF spends the same effort everywhere, which means a static scene is encoded
/// far better than it needs to be while a complex one falls short. Netflix's Dynamic
/// Optimizer showed 10-15% bitrate savings over per-title from fixing exactly that.
///
/// Searching every scene properly is accurate but costs a full CRF search per scene, so
/// the default measures each scene's complexity cheaply, searches a few that span the
/// range, and predicts the rest — complexity against CRF is close to linear in the log,
/// which is what makes a handful of calibration points enough.
/// </summary>
public sealed class PerSceneOptimizer(AppServices services, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<PerSceneOptimizer>() ?? NullLogger<PerSceneOptimizer>.Instance;

    /// <summary>Progress callback: (done, total, message).</summary>
    public event Action<int, int, string>? Progress;

    public async Task<PerSceneResult> OptimizeAsync(
        EncodeRequest request,
        ChunkPlan plan,
        PerSceneOptions options,
        MediaFileInfo info,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var target = options.ResolvedTarget;
        var (minCrf, maxCrf) = TargetQualitySearch.CrfRange(request.VideoEncoderId);

        _logger.LogInformation(
            "Per-scene optimisation: {Scenes} scene(s), {Metric} >= {Target}, {Strategy}",
            plan.Chunks.Count, options.Metric, target, options.Strategy);

        if (options.Strategy == PerSceneStrategy.Full)
            return await SearchEverySceneAsync(request, plan, options, info, started, ct);

        // ---- 1. How complex is each scene? ----
        Progress?.Invoke(0, plan.Chunks.Count, "Measuring scene complexity…");
        var complexity = await MeasureComplexityAsync(request, plan, options, ct);

        // ---- 2. Search a few scenes that span the range ----
        var calibrationIndices = ChooseCalibrationScenes(complexity, options.CalibrationScenes);
        _logger.LogInformation("Calibrating on scene(s) {Scenes}", string.Join(", ", calibrationIndices));

        var calibrated = new Dictionary<int, (int Crf, double Score)>();
        var search = new TargetQualitySearch(services, loggerFactory);

        for (var i = 0; i < calibrationIndices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var index = calibrationIndices[i];
            var chunk = plan.Chunks[index];

            Progress?.Invoke(i + 1, calibrationIndices.Count,
                $"Searching scene {index + 1}/{plan.Chunks.Count}…");

            // Search this scene alone by pointing the sampler at just its span.
            var sceneOptions = new TargetQualityOptions
            {
                Metric = options.Metric,
                Target = target,
                Samples = 1,
                SampleDuration = chunk.Duration,
                MaxProbes = 6,
                IgnoreCache = true,
            };

            var sceneInfo = SceneAsMedia(info, chunk);
            var sceneRequest = request with { InputFile = request.InputFile };

            var result = await search.SearchAsync(
                sceneRequest, sceneOptions, sceneInfo, ct, sceneOffset: chunk.StartTime);

            calibrated[index] = (result.Crf, result.PredictedScore);
            _logger.LogInformation("Scene {Index}: CRF {Crf} reaches {Score:F2}",
                index, result.Crf, result.PredictedScore);
        }

        // ---- 3. Fit complexity -> CRF and apply it to everything else ----
        var model = ComplexityModel.Fit(calibrated.Select(kv => (complexity[kv.Key], (double)kv.Value.Crf)));

        var assignments = new List<SceneAssignment>(plan.Chunks.Count);
        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            if (calibrated.TryGetValue(i, out var known))
            {
                assignments.Add(new SceneAssignment
                {
                    ChunkIndex = i,
                    Crf = known.Crf,
                    Complexity = complexity[i],
                    Calibrated = true,
                    MeasuredScore = known.Score,
                });
                continue;
            }

            var predicted = model.Predict(complexity[i]);

            // Extrapolating a straight line a long way past the fitted points is where
            // this would go wrong, so keep predictions near the searched range.
            var lo = Math.Max(minCrf, calibrated.Values.Min(v => v.Crf) - 8);
            var hi = Math.Min(maxCrf, calibrated.Values.Max(v => v.Crf) + 8);

            assignments.Add(new SceneAssignment
            {
                ChunkIndex = i,
                Crf = (int)Math.Round(Math.Clamp(predicted, lo, hi)),
                Complexity = complexity[i],
                Calibrated = false,
            });
        }

        // ---- 4. Check the model's work ----
        if (options.VerifyModelled)
            assignments = await VerifyModelledAsync(request, plan, options, info, assignments, search, ct);

        var result2 = new PerSceneResult
        {
            Assignments = assignments,
            Metric = options.Metric,
            Target = target,
            Elapsed = DateTime.UtcNow - started,
            Strategy = options.Strategy,
        };

        _logger.LogInformation("Per-scene result: {Summary}", result2.Summary);
        return result2;
    }

    /// <summary>
    /// Measure each modelled scene at the CRF it was given, and search it properly if it
    /// falls short. One measurement per scene when the model is right, a search only
    /// where it was wrong.
    /// </summary>
    private async Task<List<SceneAssignment>> VerifyModelledAsync(
        EncodeRequest request, ChunkPlan plan, PerSceneOptions options, MediaFileInfo info,
        List<SceneAssignment> assignments, TargetQualitySearch search, CancellationToken ct)
    {
        var target = options.ResolvedTarget;
        var toCheck = assignments.Where(a => !a.Calibrated).ToList();
        if (toCheck.Count == 0) return assignments;

        var measurer = new QualityMeasurer(
            services.Settings.FFmpegPath, services.Settings.FFprobePath, loggerFactory);

        var workDir = Path.Combine(services.Settings.TempDirectory, "moist-verify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        try
        {
            for (var i = 0; i < toCheck.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var assignment = toCheck[i];
                var chunk = plan.Chunks[assignment.ChunkIndex];

                Progress?.Invoke(i + 1, toCheck.Count,
                    $"Checking scene {assignment.ChunkIndex + 1}/{plan.Chunks.Count}…");

                var probeFile = Path.Combine(workDir, $"v_{assignment.ChunkIndex}.mkv");
                var frames = Math.Min(chunk.FrameCount, (long)(plan.Framerate * 4));

                double score;
                try
                {
                    await EncodeSceneSampleAsync(request, chunk, assignment.Crf, frames, probeFile, ct);

                    var measured = await measurer.MeasureAsync(
                        request.InputFile, probeFile, options.Metric,
                        new MeasureOptions
                        {
                            ReferenceStart = chunk.StartTime,
                            ReferenceDuration = TimeSpan.FromSeconds(frames / plan.Framerate),
                            ReferenceSize = info.Video is { } v ? (v.Width, v.Height) : null,
                        }, ct);

                    score = measured.Score;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not verify scene {Index}; keeping the model's answer",
                        assignment.ChunkIndex);
                    continue;
                }
                finally
                {
                    try { File.Delete(probeFile); } catch { /* best effort */ }
                }

                var index = assignments.FindIndex(a => a.ChunkIndex == assignment.ChunkIndex);

                if (score >= target - options.VerifyTolerance)
                {
                    assignments[index] = new SceneAssignment
                    {
                        ChunkIndex = assignment.ChunkIndex,
                        Crf = assignment.Crf,
                        Complexity = assignment.Complexity,
                        Calibrated = false,
                        MeasuredScore = score,
                    };
                    continue;
                }

                _logger.LogInformation(
                    "Scene {Index} modelled at CRF {Crf} only reached {Score:F2}; searching it properly",
                    assignment.ChunkIndex, assignment.Crf, score);

                var sceneOptions = new TargetQualityOptions
                {
                    Metric = options.Metric,
                    Target = target,
                    Samples = 1,
                    SampleDuration = chunk.Duration,
                    MaxProbes = 6,
                    IgnoreCache = true,
                };

                var corrected = await search.SearchAsync(
                    request, sceneOptions, SceneAsMedia(info, chunk), ct, sceneOffset: chunk.StartTime);

                assignments[index] = new SceneAssignment
                {
                    ChunkIndex = assignment.ChunkIndex,
                    Crf = corrected.Crf,
                    Complexity = assignment.Complexity,
                    Calibrated = true,
                    Corrected = true,
                    MeasuredScore = corrected.PredictedScore,
                };
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }

        return assignments;
    }

    private async Task EncodeSceneSampleAsync(
        EncodeRequest request, VideoChunk chunk, int crf, long frames, string outputPath, CancellationToken ct)
    {
        var settings = EncodeJobBuilder.CreateVideoSettings(request);
        settings.Mode = VideoEncodingMode.ConstantRateFactor;
        settings.Quality = crf;
        settings.Preset = request.Preset;
        settings.Tune = request.Tune;
        settings.BitDepth = request.BitDepth;
        settings.CustomArgs = request.VideoCustomArgs;

        var job = new Core.Jobs.VideoJob
        {
            Name = $"verify scene at crf{crf}",
            InputFile = request.InputFile,
            OutputFile = outputPath,
            EncoderId = request.VideoEncoderId,
            Settings = settings,
            FilterScript = request.FilterScript,
            FilterEngineId = request.FilterEngineId,
            TrimStart = chunk.StartTime,
            TrimFrames = frames,
        };

        var processor = services.Encoders.CreateProcessor(request.VideoEncoderId)
            ?? throw new InvalidOperationException($"no processor for encoder '{request.VideoEncoderId}'");

        if (processor is Core.Jobs.CommandLineProcessor cli)
            cli.SetLogger(services.LoggerFactory);

        await processor.ProcessAsync(job, new Progress<Core.Jobs.JobProgress>(), ct);
    }

    /// <summary>The expensive, exact path: a full search for every scene.</summary>
    private async Task<PerSceneResult> SearchEverySceneAsync(
        EncodeRequest request, ChunkPlan plan, PerSceneOptions options,
        MediaFileInfo info, DateTime started, CancellationToken ct)
    {
        var search = new TargetQualitySearch(services, loggerFactory);
        var assignments = new List<SceneAssignment>(plan.Chunks.Count);

        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = plan.Chunks[i];
            Progress?.Invoke(i + 1, plan.Chunks.Count, $"Searching scene {i + 1}/{plan.Chunks.Count}…");

            var sceneOptions = new TargetQualityOptions
            {
                Metric = options.Metric,
                Target = options.ResolvedTarget,
                Samples = 1,
                SampleDuration = chunk.Duration,
                MaxProbes = 6,
                IgnoreCache = true,
            };

            var result = await search.SearchAsync(
                request, sceneOptions, SceneAsMedia(info, chunk), ct, sceneOffset: chunk.StartTime);

            assignments.Add(new SceneAssignment
            {
                ChunkIndex = i,
                Crf = result.Crf,
                Complexity = 0,
                Calibrated = true,
                MeasuredScore = result.PredictedScore,
            });
        }

        return new PerSceneResult
        {
            Assignments = assignments,
            Metric = options.Metric,
            Target = options.ResolvedTarget,
            Elapsed = DateTime.UtcNow - started,
            Strategy = options.Strategy,
        };
    }

    /// <summary>
    /// Encoded bytes per frame at a fixed CRF, per scene. At a constant quality setting
    /// the bits a scene needs is a direct measure of how much is going on in it — a
    /// static shot compresses to nothing, a detailed moving one doesn't.
    /// </summary>
    private async Task<double[]> MeasureComplexityAsync(
        EncodeRequest request, ChunkPlan plan, PerSceneOptions options, CancellationToken ct)
    {
        var complexity = new double[plan.Chunks.Count];
        var workDir = Path.Combine(services.Settings.TempDirectory, "moist-cx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        try
        {
            for (var i = 0; i < plan.Chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = plan.Chunks[i];
                var frames = (int)Math.Min(options.ComplexityProbeFrames, chunk.FrameCount);
                var probeFile = Path.Combine(workDir, $"cx_{i}.mkv");

                // x264 ultrafast regardless of the real encoder: this is a relative
                // measure of scene content, and a fast probe keeps the cost down.
                var args = $"-y -v error -ss {chunk.SeekArgument} -i \"{request.InputFile}\" " +
                           $"-frames:v {frames} -c:v libx264 -crf {options.ComplexityProbeCrf} " +
                           $"-preset ultrafast -an \"{probeFile}\"";

                await RunAsync(services.Settings.FFmpegPath, args, ct);

                complexity[i] = File.Exists(probeFile) && frames > 0
                    ? (double)new FileInfo(probeFile).Length / frames
                    : 0;

                try { File.Delete(probeFile); } catch { /* best effort */ }

                Progress?.Invoke(i + 1, plan.Chunks.Count,
                    $"Measuring complexity {i + 1}/{plan.Chunks.Count}…");
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }

        return complexity;
    }

    /// <summary>
    /// Pick scenes spread across the complexity range rather than across the timeline —
    /// the model needs coverage of what it has to predict, and ten similar scenes teach
    /// it nothing that one would not.
    /// </summary>
    internal static List<int> ChooseCalibrationScenes(IReadOnlyList<double> complexity, int wanted)
    {
        var count = Math.Clamp(wanted, 1, complexity.Count);

        var ordered = complexity
            .Select((value, index) => (value, index))
            .OrderBy(x => x.value)
            .ToList();

        var chosen = new List<int>();
        for (var i = 0; i < count; i++)
        {
            // Evenly spaced through the sorted list, so the extremes are always included.
            var position = count == 1
                ? ordered.Count / 2
                : (int)Math.Round((double)i / (count - 1) * (ordered.Count - 1));

            var index = ordered[position].index;
            if (!chosen.Contains(index)) chosen.Add(index);
        }

        return chosen.OrderBy(i => i).ToList();
    }

    /// <summary>
    /// Present a single scene as if it were a whole file, so the search samples inside it.
    /// </summary>
    private static MediaFileInfo SceneAsMedia(MediaFileInfo info, VideoChunk chunk) => new()
    {
        FilePath = info.FilePath,
        Duration = chunk.Duration,
        FileSizeBytes = info.FileSizeBytes,
        Video = info.Video,
        AudioStreams = info.AudioStreams,
        SubtitleStreams = info.SubtitleStreams,
    };

    private static async Task RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");

        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"complexity probe failed:\n{await stderr}");
    }
}

/// <summary>
/// Straight-line fit of CRF against log complexity.
///
/// Log, because complexity spans orders of magnitude — a flat colour field and a
/// detailed moving shot can differ by a factor of hundreds — while the CRF that suits
/// them differs by only a few points. In the log domain the relationship is close
/// enough to linear for a few calibration points to cover the whole file.
/// </summary>
internal sealed class ComplexityModel
{
    private readonly double _intercept;
    private readonly double _slope;
    private readonly double _fallback;

    private ComplexityModel(double intercept, double slope, double fallback)
    {
        _intercept = intercept;
        _slope = slope;
        _fallback = fallback;
    }

    public static ComplexityModel Fit(IEnumerable<(double Complexity, double Crf)> points)
    {
        var data = points
            .Select(p => (X: Math.Log(Math.Max(p.Complexity, 1.0)), Y: p.Crf))
            .ToList();

        var meanCrf = data.Count > 0 ? data.Average(p => p.Y) : 23;

        // One point, or every scene equally complex: nothing to fit, so use the average.
        if (data.Count < 2) return new ComplexityModel(0, 0, meanCrf);

        var meanX = data.Average(p => p.X);
        var meanY = data.Average(p => p.Y);
        var varianceX = data.Sum(p => (p.X - meanX) * (p.X - meanX));

        if (varianceX < 1e-9) return new ComplexityModel(0, 0, meanCrf);

        var covariance = data.Sum(p => (p.X - meanX) * (p.Y - meanY));
        var slope = covariance / varianceX;

        return new ComplexityModel(meanY - slope * meanX, slope, meanCrf);
    }

    public double Predict(double complexity)
        => _slope == 0
            ? _fallback
            : _intercept + _slope * Math.Log(Math.Max(complexity, 1.0));

    public override string ToString()
        => _slope == 0
            ? $"constant CRF {_fallback:F1}"
            : $"CRF = {_intercept:F2} + {_slope:F2}·ln(complexity)";
}
