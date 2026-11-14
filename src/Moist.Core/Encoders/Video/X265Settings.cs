namespace Moist.Core.Encoders.Video;

public sealed class X265Settings : VideoEncoderSettings
{
    /// <summary>
    /// HEVC profile, or null to let x265 pick one that matches the bit depth.
    /// Leave it null: hardcoding "main" makes every 10-bit source fail with
    /// "main profile not supported, internal bit depth 10".
    /// </summary>
    public string? Profile { get; set; }

    public string? Level { get; set; }
    public bool Hdr { get; set; }
    public string? MasterDisplay { get; set; }
    public string? MaxCll { get; set; }

    /// <summary>Standalone x265 (cli-x265) only: see <see cref="X265StreamWrap"/>. FFmpeg's libx265 muxes
    /// with real timestamps and ignores this.</summary>
    public X265StreamWrap StreamWrap { get; set; } = X265StreamWrap.NoBFrames;
}

/// <summary>
/// How the standalone x265 encoder's raw HEVC stream gets its timestamps.
/// <para>x265 writes an elementary stream with no timing at all, and FFmpeg can only number
/// its packets in <i>decode</i> order. With B-frames, decode order is not display order, so
/// numbering them gives the presentation timestamps 0, .042, .167, .125, .209, .083… — the
/// picture decodes in the right order but players and remuxes that trust the timestamps
/// stutter or drop frames. Either B-frames go (decode order = display order, numbering is
/// exact) or a muxer that reads the stream's picture order does the wrap: mkvmerge.</para>
/// </summary>
public enum X265StreamWrap
{
    /// <summary>Encode without B-frames (<c>--bframes 0</c>) and wrap with FFmpeg. Works
    /// everywhere; files run roughly 10–20% larger at the same CRF.</summary>
    NoBFrames,

    /// <summary>Keep B-frames and wrap with mkvmerge (MKVToolNix), which derives correct
    /// timestamps from the picture order. Fails the job if mkvmerge isn't available.</summary>
    Mkvmerge,

    /// <summary>mkvmerge when it's available, otherwise <see cref="NoBFrames"/>.</summary>
    Auto,
}
