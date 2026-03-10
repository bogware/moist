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
