namespace Moist.Core.Encoders;

public sealed class EncoderInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required Type SettingsType { get; init; }
    public string? Description { get; init; }
    public string[] SupportedInputFormats { get; init; } = [];
    public string[] OutputFormats { get; init; } = [];
}
