namespace Moist.Filters;

/// <summary>
/// An ordered chain of filters to apply to video.
/// The GUI builds this chain; the filter engine translates it to a script/command.
/// </summary>
public sealed class FilterChain
{
    public List<AppliedFilter> Filters { get; } = [];

    public void Add(FilterDefinition filter, Dictionary<string, object>? paramValues = null)
    {
        Filters.Add(new AppliedFilter
        {
            Filter = filter,
            ParameterValues = paramValues ?? new()
        });
    }

    public void Remove(int index)
    {
        if (index >= 0 && index < Filters.Count)
            Filters.RemoveAt(index);
    }

    public void MoveUp(int index)
    {
        if (index > 0 && index < Filters.Count)
            (Filters[index - 1], Filters[index]) = (Filters[index], Filters[index - 1]);
    }

    public void MoveDown(int index)
    {
        if (index >= 0 && index < Filters.Count - 1)
            (Filters[index], Filters[index + 1]) = (Filters[index + 1], Filters[index]);
    }

    public void Clear() => Filters.Clear();
}

public sealed class AppliedFilter
{
    public required FilterDefinition Filter { get; init; }
    public Dictionary<string, object> ParameterValues { get; init; } = new();
}
