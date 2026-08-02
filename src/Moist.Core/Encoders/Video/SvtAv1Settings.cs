namespace Moist.Core.Encoders.Video;

/// <summary>
/// What SVT-AV1 should optimise for.
///
/// SVT-AV1 4.0 folded the community SVT-AV1-PSY work into mainline, and
/// <see cref="Vq"/> is where it landed: tuned for how the result looks rather than how
/// it scores. The encoder's own default is <see cref="Psnr"/>, which optimises for a
/// metric that correlates poorly with perception — worth changing for anything a person
/// will watch.
/// </summary>
public enum SvtAv1Tune
{
    /// <summary>Visual quality. The perceptual tuning; best default for real viewing.</summary>
    Vq = 0,

    /// <summary>PSNR. SVT-AV1's own default.</summary>
    Psnr = 1,

    Ssim = 2,

    /// <summary>Image quality, aimed at stills and AVIF.</summary>
    Iq = 3,

    MsSsim = 4,

    /// <summary>Optimise for VMAF specifically. Useful when VMAF is the acceptance test.</summary>
    Vmaf = 5,
}

public sealed class SvtAv1Settings : VideoEncoderSettings
{
    public int SvtPreset { get; set; } = 6;

    /// <summary>
    /// Null leaves SVT-AV1 on its own default (PSNR). Set <see cref="SvtAv1Tune.Vq"/>
    /// for content people will actually watch.
    /// </summary>
    public SvtAv1Tune? SvtTune { get; set; }
    public int FilmGrain { get; set; }
    public int FilmGrainDenoise { get; set; }
    public int TileRows { get; set; } = 1;
    public int TileColumns { get; set; } = 1;
    public int KeyframeInterval { get; set; } = 240;
}
