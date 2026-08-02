using System.Text.Json;
using Moist.Core.Quality;
using Moist.Services;

namespace Moist.Cli;

/// <summary>Shared option parsing for the quality features.</summary>
public static class QualityOptions
{
    public static QualityMetric Metric(CommandLine cl)
    {
        var raw = cl.String("metric");
        if (raw is null) return QualityMetric.Vmaf;

        try { return QualityMetrics.Parse(raw); }
        catch (ArgumentException ex) { throw new UsageException(ex.Message); }
    }

    /// <summary>
    /// Resolve the requested target from either --target-&lt;metric&gt; or --target-quality.
    /// Returns null when the user didn't ask for target-quality encoding at all.
    /// </summary>
    public static (QualityMetric Metric, double Target)? Target(CommandLine cl)
    {
        foreach (var metric in Enum.GetValues<QualityMetric>())
        {
            var flag = $"target-{metric.ToString().ToLowerInvariant()}";
            if (cl.Double(flag) is { } value) return (metric, value);
        }

        if (cl.Double("target-quality") is { } generic)
            return (Metric(cl), generic);

        return null;
    }

    public static TargetQualityOptions Build(CommandLine cl, QualityMetric metric, double target) => new()
    {
        Metric = metric,
        Target = target,
        Samples = cl.Int("samples") ?? 3,
        SampleDuration = TimeSpan.FromSeconds(cl.Double("sample-duration") ?? 5),
        MaxProbes = cl.Int("max-probes") ?? 8,
        IgnoreCache = cl.Flag("no-cache"),
    };
}

public static class CompareCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count < 2)
            throw new UsageException("compare needs two files: a reference and an encode");

        var reference = Path.GetFullPath(cl.Positional[0]);
        var distorted = Path.GetFullPath(cl.Positional[1]);

        if (!File.Exists(reference)) throw new UsageException($"reference not found: {reference}");
        if (!File.Exists(distorted)) throw new UsageException($"file not found: {distorted}");

        using var services = Cli.CreateServices(cl);
        var metric = QualityOptions.Metric(cl);
        var json = cl.Flag("json");

        if (!json && !cl.Flag("quiet", "q"))
            Console.Error.WriteLine($"Measuring {metric}…");

        var measurer = new QualityMeasurer(
            services.Settings.FFmpegPath, services.Settings.FFprobePath, services.LoggerFactory);

        QualityResult result;
        try
        {
            result = await measurer.MeasureAsync(reference, distorted, metric,
                new MeasureOptions { Threads = cl.Int("threads") ?? 0 }, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return Program.ExitFailed;
        }

        Cli.WarnUnknown(cl);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                metric = metric.ToString().ToLowerInvariant(),
                score = result.Score,
                min = result.Minimum,
                max = result.Maximum,
                harmonicMean = result.HarmonicMean,
                components = result.Components,
                frames = result.FramesMeasured,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                reference,
                distorted,
            }, Cli.Json));
            return Program.ExitOk;
        }

        Console.WriteLine($"{metric,-6} {metric.Format(result.Score)}");
        if (result.HarmonicMean is { } harmonic)
            Console.WriteLine($"       harmonic mean {metric.Format(harmonic)}  (penalises bad scenes)");
        if (result.Minimum is { } min)
            Console.WriteLine($"       worst frame   {metric.Format(min)}");
        if (result.Maximum is { } max)
            Console.WriteLine($"       best frame    {metric.Format(max)}");
        foreach (var (plane, value) in result.Components)
            Console.WriteLine($"       {plane}: {metric.Format(value)}");
        if (result.FramesMeasured > 0)
            Console.WriteLine($"       {result.FramesMeasured:N0} frames in {QueueRunner.Format(result.Elapsed)}");

        return Program.ExitOk;
    }
}

public static class CrfSearchCommand
{
    public static async Task<int> RunAsync(CommandLine cl, CancellationToken ct)
    {
        if (cl.Positional.Count == 0) throw new UsageException("crf-search requires an input file");

        var input = Path.GetFullPath(cl.Positional[0]);
        if (!File.Exists(input)) throw new UsageException($"input not found: {input}");

        using var services = Cli.CreateServices(cl);
        var json = cl.Flag("json");
        var quiet = cl.Flag("quiet", "q");

        var requested = QualityOptions.Target(cl);
        var metric = requested?.Metric ?? QualityOptions.Metric(cl);
        var target = requested?.Target ?? metric.DefaultTarget();

        var info = await services.MediaAnalyzer.AnalyzeAsync(input, ct);
        var (filterScript, filterEngineId) = EncodeOptions.BuildFilters(cl, services, input);
        var request = EncodeOptions.Build(
            cl, services, input, Path.ChangeExtension(input, ".search.mkv"),
            filterScript, filterEngineId, info.AudioStreams.Count);

        var options = QualityOptions.Build(cl, metric, target);
        var result = await RunSearchAsync(services, request, options, info, quiet || json, ct);

        Cli.WarnUnknown(cl);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                crf = result.Crf,
                predictedScore = result.PredictedScore,
                targetMet = result.TargetMet,
                metric = result.Metric.ToString().ToLowerInvariant(),
                target = result.Target,
                estimatedSizePercent = result.EstimatedSizePercent,
                fromCache = result.FromCache,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                probes = result.Probes.Select(p => new { p.Crf, p.Score, p.MeetsTarget }),
            }, Cli.Json));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine(result.Summary + (result.FromCache ? "  (cached)" : ""));
            if (result.EstimatedSizePercent is { } percent)
                Console.WriteLine($"Estimated size: {percent:F0}% of the source");
            if (!result.TargetMet)
                Console.Error.WriteLine("Target was not reached; the value above is the best of the probes.");
        }

        return result.TargetMet ? Program.ExitOk : Program.ExitFailed;
    }

    /// <summary>Shared by crf-search and by encode --target-*.</summary>
    public static async Task<TargetQualityResult> RunSearchAsync(
        AppServices services, EncodeRequest request, TargetQualityOptions options,
        Moist.Core.Media.MediaFileInfo info, bool quiet, CancellationToken ct)
    {
        var search = new TargetQualitySearch(services, services.LoggerFactory);

        if (!quiet)
        {
            search.Progress += (done, budget, message) =>
                Console.Error.WriteLine($"  [{done}/{budget}] {message}");
        }

        var result = await search.SearchAsync(request, options, info, ct);

        if (!quiet && !result.FromCache)
        {
            foreach (var probe in result.Probes)
            {
                var verdict = probe.MeetsTarget ? "ok  " : "low ";
                Console.Error.WriteLine(
                    $"  {verdict} CRF {probe.Crf,2}  {options.Metric.Format(probe.Score)}");
            }
        }

        return result;
    }
}
