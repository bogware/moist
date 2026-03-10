namespace Moist.Filters;

/// <summary>
/// Central registry for all filter engines. The UI queries this
/// to build the filter selection panel.
/// </summary>
public sealed class FilterRegistry
{
    private readonly List<IFilterEngine> _engines = [];

    public IReadOnlyList<IFilterEngine> Engines => _engines;

    public void Register(IFilterEngine engine)
    {
        _engines.Add(engine);
    }

    public IFilterEngine? GetEngine(string id)
        => _engines.FirstOrDefault(e => e.Id == id);

    public IReadOnlyList<FilterDefinition> GetAllFilters()
        => _engines
            .Where(e => e.IsAvailable)
            .SelectMany(e => e.GetAvailableFilters())
            .ToList();

    public async Task DetectAvailableEnginesAsync(CancellationToken ct = default)
    {
        foreach (var engine in _engines)
        {
            await engine.CheckAvailabilityAsync(ct);
        }
    }
}
