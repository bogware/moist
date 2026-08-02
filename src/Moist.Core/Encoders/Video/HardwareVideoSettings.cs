namespace Moist.Core.Encoders.Video;

/// <summary>Which hardware encoding API to drive.</summary>
public enum HardwareVendor
{
    /// <summary>NVIDIA NVENC. Best all-round quality of the hardware options.</summary>
    Nvenc,

    /// <summary>Intel Quick Sync Video.</summary>
    Qsv,

    /// <summary>AMD Advanced Media Framework.</summary>
    Amf,

    /// <summary>Apple VideoToolbox (macOS).</summary>
    VideoToolbox,

    /// <summary>VA-API (Linux). Requires a render device and an upload into GPU memory.</summary>
    Vaapi,
}

/// <summary>
/// Settings for a GPU encoder.
///
/// Hardware encoders do not understand <c>-crf</c>; each vendor has its own quality
/// control, and <see cref="VideoEncoderSettings.Quality"/> is mapped onto it. They also
/// have their own preset scales, so <see cref="VideoEncoderSettings.Preset"/> is
/// translated rather than passed through.
///
/// Expect a larger file than a software encoder at the same nominal quality: fixed-function
/// silicon skips the expensive search work that x264/x265 do.
/// </summary>
public sealed class HardwareVideoSettings : VideoEncoderSettings
{
    public required HardwareVendor Vendor { get; init; }

    /// <summary>Canonical stream codec being produced: h264, hevc or av1.</summary>
    public required string Codec { get; init; }

    /// <summary>
    /// VA-API render node. Ignored by every other vendor.
    /// </summary>
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";

    /// <summary>
    /// NVENC only: spend a second analysis pass for better bitrate distribution.
    /// This is how two-pass works on NVENC — it has no <c>-pass</c>.
    /// </summary>
    public bool NvencMultipass { get; set; } = true;

    /// <summary>
    /// Ask the encoder to prioritise quality over latency where the vendor exposes the
    /// choice. Off is only useful for live streaming.
    /// </summary>
    public bool TuneForQuality { get; set; } = true;
}
