using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Moist.Core.Media;

/// <summary>
/// Analyzes media files using ffprobe to extract stream information.
/// </summary>
public sealed class FFprobeMediaAnalyzer
{
    private readonly string _ffprobePath;

    public FFprobeMediaAnalyzer(string ffprobePath)
    {
        _ffprobePath = ffprobePath;
    }

    public async Task<MediaFileInfo> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        var args = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start ffprobe. Ensure '{_ffprobePath}' is installed and on PATH.");
        var json = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException(
                $"ffprobe failed (exit code {process.ExitCode}). Check that the input file exists and is a valid media file.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var format = root.GetProperty("format");
        var streams = root.GetProperty("streams");

        var duration = TimeSpan.Zero;
        if (format.TryGetProperty("duration", out var dur) &&
            double.TryParse(dur.GetString(), CultureInfo.InvariantCulture, out var durSec))
        {
            duration = TimeSpan.FromSeconds(durSec);
        }

        long fileSize = 0;
        if (format.TryGetProperty("size", out var size))
            long.TryParse(size.GetString(), out fileSize);

        VideoStreamInfo? video = null;
        var audioStreams = new List<AudioStreamInfo>();
        var subtitleStreams = new List<SubtitleStreamInfo>();

        foreach (var stream in streams.EnumerateArray())
        {
            var codecType = stream.GetProperty("codec_type").GetString();

            switch (codecType)
            {
                case "video" when video is null:
                    video = ParseVideoStream(stream, duration);
                    break;
                case "audio":
                    audioStreams.Add(ParseAudioStream(stream));
                    break;
                case "subtitle":
                    subtitleStreams.Add(ParseSubtitleStream(stream));
                    break;
            }
        }

        return new MediaFileInfo
        {
            FilePath = filePath,
            Duration = duration,
            FileSizeBytes = fileSize,
            Video = video,
            AudioStreams = audioStreams,
            SubtitleStreams = subtitleStreams,
        };
    }

    private static VideoStreamInfo ParseVideoStream(JsonElement stream, TimeSpan duration)
    {
        var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

        double fps = 0;
        if (stream.TryGetProperty("r_frame_rate", out var rfr))
        {
            var parts = rfr.GetString()?.Split('/');
            if (parts?.Length == 2 &&
                double.TryParse(parts[0], CultureInfo.InvariantCulture, out var num) &&
                double.TryParse(parts[1], CultureInfo.InvariantCulture, out var den) &&
                den > 0)
            {
                fps = num / den;
            }
        }

        var totalFrames = (long)(duration.TotalSeconds * fps);
        if (stream.TryGetProperty("nb_frames", out var nbf) &&
            long.TryParse(nbf.GetString(), out var parsed))
        {
            totalFrames = parsed;
        }

        var bitDepth = 8;
        if (stream.TryGetProperty("bits_per_raw_sample", out var bps))
            int.TryParse(bps.GetString(), out bitDepth);

        var pixFmt = stream.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "";
        var codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";

        string? dar = null;
        if (stream.TryGetProperty("display_aspect_ratio", out var darProp))
            dar = darProp.GetString();

        long bitrateKbps = 0;
        if (stream.TryGetProperty("bit_rate", out var br) &&
            long.TryParse(br.GetString(), out var brVal))
            bitrateKbps = brVal / 1000;

        return new VideoStreamInfo
        {
            Codec = codec,
            Width = width,
            Height = height,
            Framerate = fps,
            TotalFrames = totalFrames,
            BitDepth = bitDepth,
            PixelFormat = pixFmt,
            DisplayAspectRatio = dar,
            BitrateKbps = bitrateKbps,
        };
    }

    private static AudioStreamInfo ParseAudioStream(JsonElement stream)
    {
        return new AudioStreamInfo
        {
            Index = stream.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0,
            Codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "",
            Channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0,
            SampleRate = stream.TryGetProperty("sample_rate", out var sr) &&
                         int.TryParse(sr.GetString(), out var srVal) ? srVal : 0,
            BitrateKbps = stream.TryGetProperty("bit_rate", out var br) &&
                          long.TryParse(br.GetString(), out var brVal) ? brVal / 1000 : 0,
            Language = GetTag(stream, "language"),
            Title = GetTag(stream, "title"),
        };
    }

    private static SubtitleStreamInfo ParseSubtitleStream(JsonElement stream)
    {
        var disposition = stream.TryGetProperty("disposition", out var disp) ? disp : default;

        return new SubtitleStreamInfo
        {
            Index = stream.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0,
            Codec = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "",
            Language = GetTag(stream, "language"),
            Title = GetTag(stream, "title"),
            IsDefault = disposition.ValueKind == JsonValueKind.Object &&
                        disposition.TryGetProperty("default", out var def) && def.GetInt32() == 1,
            IsForced = disposition.ValueKind == JsonValueKind.Object &&
                       disposition.TryGetProperty("forced", out var forced) && forced.GetInt32() == 1,
        };
    }

    private static string? GetTag(JsonElement stream, string tag)
    {
        if (stream.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty(tag, out var val))
            return val.GetString();
        return null;
    }
}
