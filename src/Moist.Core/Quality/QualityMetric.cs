namespace Moist.Core.Quality;

/// <summary>Full-reference quality metrics moist can measure.</summary>
public enum QualityMetric
{
    /// <summary>
    /// Netflix's perceptual metric, 0-100. The most widely understood target,
    /// and the one to use unless you have a reason not to.
    /// </summary>
    Vmaf,

    /// <summary>
    /// Extended perceptually weighted PSNR, in dB. Temporally aware and cheap to
    /// compute, which makes it a good fit for high resolutions — but the "good"
    /// threshold depends on the content, so it's harder to target blind.
    /// </summary>
    Xpsnr,

    /// <summary>Structural similarity, 0-1. Cheap and familiar.</summary>
    Ssim,

    /// <summary>Peak signal-to-noise ratio, in dB. Correlates poorly with perception.</summary>
    Psnr,
}

public static class QualityMetrics
{
    /// <summary>A sensible "visually transparent enough" target for each metric.</summary>
    public static double DefaultTarget(this QualityMetric metric) => metric switch
    {
        QualityMetric.Vmaf => 95.0,
        QualityMetric.Xpsnr => 40.0,
        QualityMetric.Ssim => 0.98,
        QualityMetric.Psnr => 42.0,
        _ => 95.0,
    };

    /// <summary>Every supported metric improves as the number rises.</summary>
    public static bool HigherIsBetter(this QualityMetric _) => true;

    public static string Unit(this QualityMetric metric) => metric switch
    {
        QualityMetric.Xpsnr or QualityMetric.Psnr => "dB",
        _ => "",
    };

    /// <summary>How many decimals are meaningful when showing a score.</summary>
    public static int Precision(this QualityMetric metric) => metric switch
    {
        QualityMetric.Ssim => 4,
        _ => 2,
    };

    public static string Format(this QualityMetric metric, double score)
    {
        var unit = metric.Unit();
        var text = score.ToString($"F{metric.Precision()}", System.Globalization.CultureInfo.InvariantCulture);
        return unit.Length > 0 ? $"{text} {unit}" : text;
    }

    public static QualityMetric Parse(string raw) => raw.ToLowerInvariant() switch
    {
        "vmaf" => QualityMetric.Vmaf,
        "xpsnr" => QualityMetric.Xpsnr,
        "ssim" => QualityMetric.Ssim,
        "psnr" => QualityMetric.Psnr,
        _ => throw new ArgumentException($"unknown metric '{raw}' (expected vmaf, xpsnr, ssim or psnr)"),
    };
}
