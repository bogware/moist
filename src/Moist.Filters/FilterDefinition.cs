namespace Moist.Filters;

/// <summary>
/// Represents a single filter that can be applied to video.
/// Engine-agnostic — each filter engine translates this to its own syntax.
/// </summary>
public sealed class FilterDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required FilterCategory Category { get; init; }
    public string Description { get; init; } = string.Empty;
    public Dictionary<string, FilterParameter> Parameters { get; init; } = new();
}

public enum FilterCategory
{
    Source,
    Crop,
    Resize,
    Deinterlace,
    Denoise,
    Sharpen,
    ColorCorrection,
    FrameRate,
    Subtitle,
    Other,
    Custom
}

public sealed class FilterParameter
{
    public required string Name { get; init; }
    public required FilterParameterType Type { get; init; }
    public object? DefaultValue { get; init; }
    public object? MinValue { get; init; }
    public object? MaxValue { get; init; }
    public string[]? Options { get; init; }
    public string Description { get; init; } = string.Empty;
}

public enum FilterParameterType
{
    Int,
    Double,
    Bool,
    String,
    Choice
}
