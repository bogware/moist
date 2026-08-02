namespace Moist.Core.Quality;

/// <summary>The outcome of comparing an encode against its source.</summary>
public sealed class QualityResult
{
    public required QualityMetric Metric { get; init; }

    /// <summary>
    /// The headline number: the pooled mean. This is what a target is compared against.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>Worst frame. Often more telling than the mean — a good average can hide one terrible scene.</summary>
    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    /// <summary>
    /// Harmonic mean, which penalises low outliers far more than the arithmetic mean.
    /// Netflix pools VMAF this way for exactly that reason.
    /// </summary>
    public double? HarmonicMean { get; init; }

    /// <summary>Per-plane scores where the metric reports them (y, u, v).</summary>
    public IReadOnlyDictionary<string, double> Components { get; init; }
        = new Dictionary<string, double>();

    public long FramesMeasured { get; init; }
    public TimeSpan Elapsed { get; init; }

    public string Summary
    {
        get
        {
            var text = $"{Metric} {Metric.Format(Score)}";
            if (Minimum is { } min) text += $" (worst frame {Metric.Format(min)})";
            return text;
        }
    }
}
