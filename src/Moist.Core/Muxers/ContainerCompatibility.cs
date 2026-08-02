namespace Moist.Core.Muxers;

/// <summary>
/// Which elementary streams each container can actually carry when muxed with <c>-c copy</c>.
///
/// The tables below were derived empirically against FFmpeg by muxing every
/// (container, video, audio) combination and re-probing the result to confirm the
/// streams survived intact. Exit codes alone are not trustworthy here — muxing HEVC
/// into AVI, for example, "succeeds" but lands as an unplayable <c>rawvideo</c> stream.
///
/// Codec names are the canonical FFmpeg stream names (h264, hevc, av1, …), not the
/// encoder library names (libx264, libx265, libsvtav1, …).
/// </summary>
public static class ContainerCompatibility
{
    private static readonly Dictionary<ContainerFormat, string[]> VideoSupport = new()
    {
        [ContainerFormat.Mkv] = ["h264", "hevc", "vp9", "av1", "ffv1"],
        [ContainerFormat.Mp4] = ["h264", "hevc", "vp9", "av1", "ffv1"],
        [ContainerFormat.Webm] = ["vp9", "av1"],
        [ContainerFormat.Avi] = ["h264", "vp9", "av1", "ffv1"],
        [ContainerFormat.Ts] = ["h264", "hevc"],
        [ContainerFormat.Mov] = ["h264", "hevc", "ffv1"],
    };

    private static readonly Dictionary<ContainerFormat, string[]> AudioSupport = new()
    {
        [ContainerFormat.Mkv] = ["aac", "opus", "mp3", "flac", "ac3", "eac3", "vorbis"],
        [ContainerFormat.Mp4] = ["aac", "opus", "mp3", "flac", "ac3", "eac3", "vorbis"],
        [ContainerFormat.Webm] = ["opus", "vorbis"],
        [ContainerFormat.Avi] = ["aac", "mp3", "flac", "ac3", "eac3", "vorbis"],
        [ContainerFormat.Ts] = ["aac", "mp3", "ac3", "eac3", "opus"],
        [ContainerFormat.Mov] = ["aac", "mp3", "ac3", "eac3", "vorbis"],
    };

    /// <summary>
    /// Maps an encoder id (or the short codec key used at registration) to the canonical
    /// FFmpeg stream codec name. Returns null when the output codec can't be known ahead
    /// of time — passthrough copies whatever the source happens to contain.
    /// </summary>
    public static string? StreamCodecForEncoder(string? encoderId)
    {
        if (string.IsNullOrWhiteSpace(encoderId)) return null;

        // Hardware encoder ids carry their codec: hw-<vendor>-<codec>.
        if (encoderId.StartsWith("hw-", StringComparison.Ordinal))
        {
            var codec = encoderId[(encoderId.LastIndexOf('-') + 1)..];
            return codec is "h264" or "hevc" or "av1" or "vp9" ? codec : null;
        }

        return encoderId switch
        {
            "ffmpeg-x264" or "cli-x264" or "x264" => "h264",
            "ffmpeg-x265" or "cli-x265" or "x265" => "hevc",
            "ffmpeg-svtav1" or "svtav1" => "av1",
            "ffmpeg-vp9" or "vp9" => "vp9",
            "ffmpeg-ffv1" or "ffv1" => "ffv1",

            "ffmpeg-audio-opus" or "opus" => "opus",
            "ffmpeg-audio-aac" or "aac" => "aac",
            "ffmpeg-audio-mp3" or "mp3" => "mp3",
            "ffmpeg-audio-flac" or "flac" => "flac",
            "ffmpeg-audio-ac3" or "ac3" => "ac3",
            "ffmpeg-audio-eac3" or "eac3" => "eac3",
            "ffmpeg-audio-vorbis" or "vorbis" => "vorbis",

            // Passthrough — the resulting codec depends on the source file.
            "ffmpeg-audio-copy" or "copy" => null,
            _ => null,
        };
    }

    /// <summary>True if the container can carry this video codec. Unknown codecs are permitted.</summary>
    public static bool SupportsVideo(ContainerFormat container, string? streamCodec)
        => streamCodec is null
           || !VideoSupport.TryGetValue(container, out var ok)
           || ok.Contains(streamCodec, StringComparer.OrdinalIgnoreCase);

    /// <summary>True if the container can carry this audio codec. Unknown codecs are permitted.</summary>
    public static bool SupportsAudio(ContainerFormat container, string? streamCodec)
        => streamCodec is null
           || !AudioSupport.TryGetValue(container, out var ok)
           || ok.Contains(streamCodec, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validate a planned mux. Returns null when the combination is fine, otherwise a
    /// message naming the offending stream and suggesting containers that would work.
    /// </summary>
    public static string? Validate(ContainerFormat container, string? videoCodec, string? audioCodec)
    {
        var videoOk = SupportsVideo(container, videoCodec);
        var audioOk = SupportsAudio(container, audioCodec);
        if (videoOk && audioOk) return null;

        var offending = !videoOk && !audioOk
            ? $"video ({videoCodec}) and audio ({audioCodec}) streams are"
            : !videoOk
                ? $"video stream ({videoCodec}) is"
                : $"audio stream ({audioCodec}) is";

        var alternatives = SuggestContainers(videoCodec, audioCodec);
        var suggestion = alternatives.Count > 0
            ? $" Use {JoinReadable(alternatives)} instead, or change the codec."
            : " Change the video or audio codec.";

        return $"The {offending} not supported by the {container} container.{suggestion}";
    }

    /// <summary>"Mkv", "Mkv or Mp4", "Mkv, Mp4 or Mov" — for error messages.</summary>
    private static string JoinReadable(IReadOnlyList<ContainerFormat> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0].ToString(),
        _ => string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1],
    };

    /// <summary>Containers that can carry both codecs, most broadly compatible first.</summary>
    public static IReadOnlyList<ContainerFormat> SuggestContainers(string? videoCodec, string? audioCodec)
    {
        // Deliberate order: MKV carries essentially anything, MP4 is the most portable.
        ContainerFormat[] preference =
        [
            ContainerFormat.Mkv, ContainerFormat.Mp4, ContainerFormat.Mov,
            ContainerFormat.Webm, ContainerFormat.Ts, ContainerFormat.Avi,
        ];

        return preference
            .Where(c => SupportsVideo(c, videoCodec) && SupportsAudio(c, audioCodec))
            .ToList();
    }

    /// <summary>
    /// Bitstream filter needed to remux a codec into this container, if any.
    ///
    /// H.264/H.265 live in MP4/Matroska as length-prefixed AVCC/HVCC. AVI and MPEG-TS
    /// need Annex-B start codes, so remuxing from our intermediate .mkv without this
    /// fails with "bitstream malformed, no startcode found".
    /// </summary>
    public static string? VideoBitstreamFilter(ContainerFormat container, string? streamCodec)
    {
        if (container is not (ContainerFormat.Avi or ContainerFormat.Ts)) return null;

        return streamCodec?.ToLowerInvariant() switch
        {
            "h264" => "h264_mp4toannexb",
            "hevc" => "hevc_mp4toannexb",
            _ => null,
        };
    }
}
