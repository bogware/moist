namespace Moist.Filters;

/// <summary>
/// Translates a FilterChain into an engine-specific script or command.
/// Each engine (FFmpeg, VapourSynth, AviSynth) implements this interface.
/// </summary>
public interface IFilterEngine
{
    string Id { get; }
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>
    /// Get all filters supported by this engine.
    /// </summary>
    IReadOnlyList<FilterDefinition> GetAvailableFilters();

    /// <summary>
    /// Translate a filter chain into a script string.
    /// For FFmpeg: returns a -filter_complex argument.
    /// For VapourSynth: returns a .vpy script.
    /// For AviSynth: returns a .avs script.
    /// </summary>
    string GenerateScript(FilterChain chain, string inputFile);

    /// <summary>
    /// Check if the engine and its required tools are installed.
    /// </summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken ct = default);
}
