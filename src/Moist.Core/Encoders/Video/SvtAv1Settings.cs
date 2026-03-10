namespace Moist.Core.Encoders.Video;

public sealed class SvtAv1Settings : VideoEncoderSettings
{
    public int SvtPreset { get; set; } = 6;
    public int FilmGrain { get; set; }
    public int FilmGrainDenoise { get; set; }
    public int TileRows { get; set; } = 1;
    public int TileColumns { get; set; } = 1;
    public int KeyframeInterval { get; set; } = 240;
}
