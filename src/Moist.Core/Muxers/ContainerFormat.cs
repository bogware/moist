namespace Moist.Core.Muxers;

public enum ContainerFormat
{
    Mkv,
    Mp4,
    Webm,
    Avi,
    Ts,
    Mov
}

public static class ContainerFormatExtensions
{
    public static string GetExtension(this ContainerFormat format) => format switch
    {
        ContainerFormat.Mkv => ".mkv",
        ContainerFormat.Mp4 => ".mp4",
        ContainerFormat.Webm => ".webm",
        ContainerFormat.Avi => ".avi",
        ContainerFormat.Ts => ".ts",
        ContainerFormat.Mov => ".mov",
        _ => ".mkv"
    };

    /// <summary>
    /// Maps a filename (or a bare extension like "mp4"/".mp4") back to a container, or null when
    /// unrecognized so the caller can keep its own default. This is what lets <c>-o out.mp4</c>
    /// actually produce MP4 instead of Matroska with the wrong extension — and lets container
    /// validation reject a doomed codec/container pair up front.
    /// </summary>
    public static ContainerFormat? FromExtension(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension)) return null;
        var ext = Path.GetExtension(pathOrExtension);
        if (string.IsNullOrEmpty(ext)) ext = pathOrExtension;
        ext = ext.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mkv" => ContainerFormat.Mkv,
            "mp4" or "m4v" => ContainerFormat.Mp4,
            "webm" => ContainerFormat.Webm,
            "avi" => ContainerFormat.Avi,
            "ts" or "m2ts" or "mts" or "mpegts" => ContainerFormat.Ts,
            "mov" => ContainerFormat.Mov,
            _ => null
        };
    }

    public static string GetFfmpegFormat(this ContainerFormat format) => format switch
    {
        ContainerFormat.Mkv => "matroska",
        ContainerFormat.Mp4 => "mp4",
        ContainerFormat.Webm => "webm",
        ContainerFormat.Avi => "avi",
        ContainerFormat.Ts => "mpegts",
        ContainerFormat.Mov => "mov",
        _ => "matroska"
    };
}
