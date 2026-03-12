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

    /// <summary>
    /// Validate parameter values against their definitions.
    /// Returns a list of validation errors, empty if valid.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (key, paramDef) in Filter.Parameters)
        {
            if (!ParameterValues.TryGetValue(key, out var value))
                continue; // Missing params use defaults — OK

            try
            {
                switch (paramDef.Type)
                {
                    case FilterParameterType.Int:
                        var intVal = Convert.ToInt32(value);
                        if (paramDef.MinValue is not null && intVal < Convert.ToInt32(paramDef.MinValue))
                            errors.Add($"{paramDef.Name}: value {intVal} is below minimum {paramDef.MinValue}");
                        if (paramDef.MaxValue is not null && intVal > Convert.ToInt32(paramDef.MaxValue))
                            errors.Add($"{paramDef.Name}: value {intVal} is above maximum {paramDef.MaxValue}");
                        break;

                    case FilterParameterType.Double:
                        var dblVal = Convert.ToDouble(value);
                        if (paramDef.MinValue is not null && dblVal < Convert.ToDouble(paramDef.MinValue))
                            errors.Add($"{paramDef.Name}: value {dblVal} is below minimum {paramDef.MinValue}");
                        if (paramDef.MaxValue is not null && dblVal > Convert.ToDouble(paramDef.MaxValue))
                            errors.Add($"{paramDef.Name}: value {dblVal} is above maximum {paramDef.MaxValue}");
                        break;

                    case FilterParameterType.Choice:
                        var strVal = value.ToString();
                        if (paramDef.Options is not null && strVal is not null && !paramDef.Options.Contains(strVal))
                            errors.Add($"{paramDef.Name}: '{strVal}' is not a valid option");
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{paramDef.Name}: invalid value type — {ex.Message}");
            }
        }
        return errors;
    }
}
