using Moist.Core.Profiles;
using Moist.Filters;

namespace Moist.Services;

/// <summary>
/// Renders a profile's post-AI-upscale finish (<see cref="EncodingProfile.AiFinishFilters"/>) into
/// the FFmpeg filter string the Real-ESRGAN processor applies. Shared by the GUI and the CLI so a
/// profile finishes identically from either.
/// </summary>
public static class ProfileFinish
{
    /// <summary>The finish as an FFmpeg <c>-vf</c> fragment, or null when the profile has none
    /// (the processor then uses its default light finish).</summary>
    public static string? Render(EncodingProfile? profile, FilterRegistry filters)
    {
        if (profile is null || profile.AiFinishFilters.Count == 0) return null;
        var engine = filters.Engines.FirstOrDefault(e => e.Id == "ffmpeg");
        if (engine is null) return null;

        var available = engine.GetAvailableFilters();
        var chain = new FilterChain();
        foreach (var pf in profile.AiFinishFilters)
        {
            var def = available.FirstOrDefault(f => string.Equals(f.Id, pf.FilterId, StringComparison.OrdinalIgnoreCase));
            if (def is not null) chain.Add(def, new Dictionary<string, object>(pf.Parameters));
        }
        if (chain.Filters.Count == 0) return null;
        var script = engine.GenerateScript(chain, "");
        return string.IsNullOrWhiteSpace(script) ? null : script.Trim().Trim(',');
    }
}
