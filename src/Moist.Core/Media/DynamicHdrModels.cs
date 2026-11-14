namespace Moist.Core.Media;

/// <summary>
/// The result of an attempted dynamic-HDR passthrough. See <see cref="DynamicHdrPreserver"/>.
/// </summary>
public enum DynamicHdrOutcome
{
    /// <summary>The RPU / HDR10+ metadata was carried over and the encoded file was replaced.</summary>
    Preserved,

    /// <summary>Nothing to do — the source carried no dynamic HDR metadata (or none was requested).</summary>
    Skipped,

    /// <summary>A required external tool (dovi_tool / hdr10plus_tool) was not configured or not found.</summary>
    ToolMissing,

    /// <summary>An extract/inject/remux step failed. The encoded file was left untouched.</summary>
    Failed,
}

/// <summary>
/// Outcome of <see cref="DynamicHdrPreserver.PreserveAsync"/>. A value type-free record so callers
/// can log <see cref="Message"/> verbatim. <see cref="Preserved"/> is the only success outcome.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">A human-readable, log-ready explanation (never null).</param>
public sealed record DynamicHdrResult(DynamicHdrOutcome Outcome, string Message)
{
    /// <summary>True only when the metadata was actually carried over into the encoded file.</summary>
    public bool Succeeded => Outcome == DynamicHdrOutcome.Preserved;

    /// <summary>The metadata was preserved and the encoded file replaced in place.</summary>
    public static DynamicHdrResult Preserved(string message) =>
        new(DynamicHdrOutcome.Preserved, message);

    /// <summary>There was nothing to preserve.</summary>
    public static DynamicHdrResult Skipped(string message) =>
        new(DynamicHdrOutcome.Skipped, message);

    /// <summary>A required tool was absent, so the encoded file was left as-is.</summary>
    public static DynamicHdrResult ToolMissing(string message) =>
        new(DynamicHdrOutcome.ToolMissing, message);

    /// <summary>A step failed; the encoded file was left as-is.</summary>
    public static DynamicHdrResult Failed(string message) =>
        new(DynamicHdrOutcome.Failed, message);
}
