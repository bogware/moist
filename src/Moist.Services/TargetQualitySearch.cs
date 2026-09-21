using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Quality;

namespace Moist.Services;

public sealed class TargetQualityOptions
{
    public QualityMetric Metric { get; init; } = QualityMetric.Vmaf;

    /// <summary>The score to reach. Defaults to the metric's own sensible target.</summary>
    public double? Target { get; init; }

    /// <summary>How many places in the source to sample. More is slower but steadier.</summary>
    public int Samples { get; init; } = 3;

    public TimeSpan SampleDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Give up after this many probes even if the bounds haven't converged.</summary>
    public int MaxProbes { get; init; } = 8;

    /// <summary>Stop once the bracket is this narrow.</summary>
    public int Tolerance { get; init; } = 1;

    /// <summary>Ignore results from a previous run for this file.</summary>
    public bool IgnoreCache { get; init; }

    public double ResolvedTarget => Target ?? Metric.DefaultTarget();
}

/// <summary>One CRF that was tried, and what it scored.</summary>
public sealed class QualityProbe
{
    public required int Crf { get; init; }
    public required double Score { get; init; }
    public long SampleBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool MeetsTarget { get; init; }
}

public sealed class TargetQualityResult
{
    public required int Crf { get; init; }
    public required double PredictedScore { get; init; }
    public required bool TargetMet { get; init; }
    public required QualityMetric Metric { get; init; }
    public required double Target { get; init; }
    public List<QualityProbe> Probes { get; init; } = [];
    public TimeSpan Elapsed { get; init; }
    public bool FromCache { get; init; }

    /// <summary>Encoded sample size as a share of the same span of the source, when known.</summary>
    public double? EstimatedSizePercent { get; init; }

    public string Summary => TargetMet
        ? $"CRF {Crf} reaches {Metric.Format(PredictedScore)} (target {Metric.Format(Target)})"
        : $"CRF {Crf} only reaches {Metric.Format(PredictedScore)}, short of {Metric.Format(Target)}";
}

/// <summary>
/// Finds the CRF that just meets a quality target.
///
/// Encoding the whole file at each candidate would be absurd, so this samples a few
/// short spans instead and searches over those — the same approach ab-av1 uses. A
/// handful of five-second samples predicts the full encode closely enough to pick a
/// CRF, at a tiny fraction of the cost.
///
/// The search is an interpolated bisection: rather than blindly halving the bracket it
/// fits a line through the two nearest measurements and jumps to where that line
/// crosses the target, which usually lands within a point or two immediately.
///
/// Search always uses the encoder that will do the real encode. Probing with a fast
/// proxy (say NVENC standing in for x265) was considered and rejected: the CRF that
/// hits VMAF 95 on one encoder does not hit it on another, so the result would need a
/// per-encoder calibration that is itself content-dependent. Sampling already provides
/// the speed, without trading away correctness.
/// </summary>
public sealed class TargetQualitySearch(AppServices services, ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<TargetQualitySearch>() ?? NullLogger<TargetQualitySearch>.Instance;

    /// <summary>Progress callback: (probesDone, probeBudget, message).</summary>
    public event Action<int, int, string>? Progress;

    /// <param name="sceneOffset">
    /// Shift every sample by this much into the source. Per-scene optimisation uses it to
    /// search inside one scene: the caller passes a <see cref="MediaFileInfo"/> describing
    /// just that scene's duration, and this positions the samples at the right place in
    /// the real file.
    /// </param>
    public async Task<TargetQualityResult> SearchAsync(
        EncodeRequest request,
        TargetQualityOptions options,
        MediaFileInfo? info = null,
        CancellationToken ct = default,
        TimeSpan? sceneOffset = null)
    {
        // Guard the probe budget and sample length: a non-positive MaxProbes leaves the
        // probe loop with nothing to select from and would throw an opaque "Sequence
        // contains no elements", and a non-positive SampleDuration produces zero-length
        // samples that fail to encode. Fail fast with a clear message instead.
        if (options.MaxProbes < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxProbes, "MaxProbes must be at least 1");
        if (options.SampleDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.SampleDuration, "SampleDuration must be greater than zero");

        var started = DateTime.UtcNow;
        info ??= await services.MediaAnalyzer.AnalyzeAsync(request.InputFile, ct);

        var target = options.ResolvedTarget;
        var cache = new QualityCache();
        var cacheKey = QualityCache.BuildKey(request, options);

        if (!options.IgnoreCache && cache.TryGet(cacheKey, out var cached))
        {
            _logger.LogInformation("Reusing cached search for {File}: CRF {Crf}", request.InputFile, cached!.Crf);
            return new TargetQualityResult
            {
                Crf = cached.Crf,
                PredictedScore = cached.Score,
                TargetMet = cached.TargetMet,
                Metric = options.Metric,
                Target = target,
                FromCache = true,
            };
        }

        var (minCrf, maxCrf) = CrfRange(request.VideoEncoderId);
        var samples = PlanSamples(info.Duration, options);

        if (sceneOffset is { } offset && offset > TimeSpan.Zero)
            samples = samples.Select(s => s with { Start = s.Start + offset }).ToList();

        _logger.LogInformation(
            "Target-quality search on {File}: {Metric} >= {Target}, CRF {Min}-{Max}, {Samples} sample(s)",
            Path.GetFileName(request.InputFile), options.Metric, target, minCrf, maxCrf, samples.Count);

        // A geometry-changing filter (crop/pad/transpose/rotate/flip) alters frame dimensions,
        // but each sample is measured against the untouched source at its native size — so the
        // score is skewed and the chosen CRF only approximate. Non-geometry filters (scale,
        // denoise, sharpen) measure correctly because the metric rescales before comparing.
        if (HasGeometryFilter(request.FilterScript))
            _logger.LogWarning(
                "The filter chain changes frame geometry (crop/pad/rotate/flip); target-quality " +
                "sampling compares against the un-filtered source, so the predicted {Metric} may be " +
                "off and the resulting CRF approximate.", options.Metric);

        var probes = new List<QualityProbe>();
        var lo = minCrf;   // known to meet the target (or the floor)
        var hi = maxCrf;   // known to miss it (or the ceiling)

        // Start near the middle, biased low: most content meets a sane target well
        // before the midpoint, and starting there tends to save a probe.
        var candidate = (int)Math.Round(minCrf + (maxCrf - minCrf) * 0.45);

        while (probes.Count < options.MaxProbes && !ct.IsCancellationRequested)
        {
            candidate = Math.Clamp(candidate, minCrf, maxCrf);
            if (probes.Any(p => p.Crf == candidate)) break;

            Progress?.Invoke(probes.Count + 1, options.MaxProbes, $"Trying CRF {candidate}…");

            var probe = await ProbeAsync(request, options, samples, candidate, info, ct);
            probes.Add(probe);

            _logger.LogInformation("CRF {Crf} scored {Score:F2} ({Verdict})",
                probe.Crf, probe.Score, probe.MeetsTarget ? "meets target" : "below target");

            if (probe.MeetsTarget) lo = candidate; else hi = candidate;

            // We want the highest CRF (smallest file) that still meets the target.
            if (hi - lo <= options.Tolerance) break;

            candidate = NextCandidate(probes, target, lo, hi);
        }

        ct.ThrowIfCancellationRequested();

        var passing = probes.Where(p => p.MeetsTarget).OrderByDescending(p => p.Crf).ToList();
        var chosen = passing.FirstOrDefault();

        // Nothing met the target: fall back to the best score seen so the caller still
        // gets a usable answer rather than an error.
        var best = chosen ?? probes.OrderByDescending(p => p.Score).First();

        var result = new TargetQualityResult
        {
            Crf = best.Crf,
            PredictedScore = best.Score,
            TargetMet = chosen is not null,
            Metric = options.Metric,
            Target = target,
            Probes = probes.OrderBy(p => p.Crf).ToList(),
            Elapsed = DateTime.UtcNow - started,
            EstimatedSizePercent = EstimateSizePercent(best, samples, info),
        };

        // Scene-scoped searches (per-scene optimiser) pass a sceneOffset and IgnoreCache;
        // their cache key can't capture the offset, so storing them would both pollute the
        // shared cache and let a later whole-file search misread a scene's CRF as the answer.
        if (sceneOffset is null || sceneOffset.Value == TimeSpan.Zero)
            cache.Store(cacheKey, result);
        return result;
    }

    /// <summary>Encode every sample at this CRF and average the scores.</summary>
    private async Task<QualityProbe> ProbeAsync(
        EncodeRequest request,
        TargetQualityOptions options,
        IReadOnlyList<SampleSpan> samples,
        int crf,
        MediaFileInfo info,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var scores = new List<double>();
        long totalBytes = 0;

        var measurer = new QualityMeasurer(
            services.Settings.FFmpegPath, services.Settings.FFprobePath, loggerFactory: null);

        var workDir = Path.Combine(services.Settings.TempDirectory, "moist-search-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);

        try
        {
            for (var i = 0; i < samples.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sample = samples[i];
                var samplePath = Path.Combine(workDir, $"s{i}_crf{crf}.mkv");

                await EncodeSampleAsync(request, crf, sample, samplePath, info, ct);

                var measurement = await measurer.MeasureAsync(
                    request.InputFile, samplePath, options.Metric,
                    new MeasureOptions
                    {
                        ReferenceStart = sample.Start,
                        ReferenceDuration = sample.Duration,
                        ReferenceSize = info.Video is { } v ? (v.Width, v.Height) : null,
                    },
                    ct);

                scores.Add(measurement.Score);
                totalBytes += new FileInfo(samplePath).Length;
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }

        // Average across samples. A single hard scene shouldn't dominate the decision,
        // but neither should it be ignored — this is why several samples are taken.
        var score = scores.Average();

        return new QualityProbe
        {
            Crf = crf,
            Score = score,
            SampleBytes = totalBytes,
            Elapsed = DateTime.UtcNow - started,
            MeetsTarget = score >= options.ResolvedTarget,
        };
    }

    /// <summary>Run one sample through the real encoder, so the probe reflects real settings.</summary>
    private async Task EncodeSampleAsync(
        EncodeRequest request, int crf, SampleSpan sample, string outputPath, MediaFileInfo info, CancellationToken ct)
    {
        var settings = EncodeJobBuilder.CreateVideoSettings(request);
        settings.Mode = VideoEncodingMode.ConstantRateFactor;
        settings.Quality = crf;
        settings.Preset = request.Preset;
        settings.Tune = request.Tune;
        settings.BitDepth = request.BitDepth;
        settings.CustomArgs = request.VideoCustomArgs;

        var job = new VideoJob
        {
            Name = $"sample crf{crf}",
            InputFile = request.InputFile,
            OutputFile = outputPath,
            EncoderId = request.VideoEncoderId,
            Settings = settings,
            FilterScript = request.FilterScript,
            FilterEngineId = request.FilterEngineId,
            TrimStart = sample.Start,
            TrimDuration = sample.Duration,
            TotalFrames = (long)(sample.Duration.TotalSeconds * (info.Video?.Framerate ?? 25)),
        };

        var processor = services.Encoders.CreateProcessor(request.VideoEncoderId)
            ?? throw new InvalidOperationException($"no processor for encoder '{request.VideoEncoderId}'");

        if (processor is CommandLineProcessor cli)
            cli.SetLogger(services.LoggerFactory);

        await processor.ProcessAsync(job, new Progress<JobProgress>(), ct);
    }

    /// <summary>
    /// Where the next probe should go. Interpolating between the two measurements that
    /// bracket the target converges much faster than plain bisection, because quality
    /// against CRF is close to linear over any short span.
    /// </summary>
    internal static int NextCandidate(IReadOnlyList<QualityProbe> probes, double target, int lo, int hi)
    {
        var below = probes.Where(p => p.Score < target).OrderByDescending(p => p.Score).FirstOrDefault();
        var above = probes.Where(p => p.Score >= target).OrderBy(p => p.Score).FirstOrDefault();

        if (below is not null && above is not null && Math.Abs(below.Score - above.Score) > 0.001)
        {
            var slope = (below.Crf - above.Crf) / (below.Score - above.Score);
            var guess = (int)Math.Round(above.Crf + (target - above.Score) * slope);

            // Interpolation can overshoot on noisy data; keep it strictly inside the bracket.
            if (guess > lo && guess < hi) return guess;
        }

        return lo + (hi - lo) / 2;
    }

    /// <summary>
    /// Pick evenly spread spans, skipping the outer 5% where titles and credits live —
    /// black frames and logos score nothing like the actual content.
    /// </summary>
    internal static List<SampleSpan> PlanSamples(TimeSpan duration, TargetQualityOptions options)
    {
        var total = duration.TotalSeconds;
        var sampleSeconds = options.SampleDuration.TotalSeconds;

        // Very short sources: just take the whole thing once.
        if (total <= sampleSeconds * 1.5)
            return [new SampleSpan(TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(1, total)))];

        var usableStart = total * 0.05;
        var usableEnd = total * 0.95;
        var usable = usableEnd - usableStart;

        var count = Math.Max(1, options.Samples);

        // Don't ask for more samples than actually fit.
        count = (int)Math.Min(count, Math.Max(1, Math.Floor(usable / sampleSeconds)));

        var spans = new List<SampleSpan>(count);
        for (var i = 0; i < count; i++)
        {
            // Centre each sample within its own equal slice of the usable range.
            var slice = usable / count;
            var centre = usableStart + slice * (i + 0.5);
            var start = Math.Max(0, centre - sampleSeconds / 2);
            var length = Math.Min(sampleSeconds, total - start);
            spans.Add(new SampleSpan(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(length)));
        }

        return spans;
    }

    /// <summary>Encoded sample size relative to the same span of the source.</summary>
    private static double? EstimateSizePercent(QualityProbe probe, IReadOnlyList<SampleSpan> samples, MediaFileInfo info)
    {
        if (probe.SampleBytes <= 0 || info.FileSizeBytes <= 0 || info.Duration.TotalSeconds <= 0) return null;

        var sampledSeconds = samples.Sum(s => s.Duration.TotalSeconds);
        if (sampledSeconds <= 0) return null;

        var sourceBytesForSpan = info.FileSizeBytes * (sampledSeconds / info.Duration.TotalSeconds);
        if (sourceBytesForSpan <= 0) return null;

        return probe.SampleBytes / sourceBytesForSpan * 100.0;
    }

    /// <summary>
    /// Whether a rendered FFmpeg filter script contains a filter that changes frame geometry,
    /// which would make measuring the sample against the un-filtered source unreliable.
    /// </summary>
    internal static bool HasGeometryFilter(string? filterScript)
    {
        if (string.IsNullOrWhiteSpace(filterScript)) return false;
        string[] geometry = ["crop", "pad", "transpose", "rotate", "hflip", "vflip"];
        return geometry.Any(f =>
            filterScript.Contains(f + "=", StringComparison.OrdinalIgnoreCase) ||
            filterScript.Contains(f + ",", StringComparison.OrdinalIgnoreCase) ||
            filterScript.EndsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Valid CRF range for the encoder. AV1 and VP9 go to 63; H.264/H.265 stop at 51.</summary>
    internal static (int Min, int Max) CrfRange(string encoderId) => encoderId switch
    {
        "ffmpeg-svtav1" or "ffmpeg-vp9" => (10, 55),
        _ when encoderId.EndsWith("-av1", StringComparison.Ordinal) => (10, 55),
        _ => (10, 45),
    };
}

/// <summary>A span of the source used as a search sample.</summary>
public readonly record struct SampleSpan(TimeSpan Start, TimeSpan Duration)
{
    public override string ToString()
        => $"{Start.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s +{Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s";
}
