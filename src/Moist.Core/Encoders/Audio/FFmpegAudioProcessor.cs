using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Audio;

/// <summary>
/// FFmpeg-based audio encoder processor. Supports AAC, Opus, FLAC, MP3, etc.
/// </summary>
public sealed partial class FFmpegAudioProcessor : CommandLineProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _codec;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => $"ffmpeg-audio-{_codec}";
    public override string DisplayName => $"FFmpeg Audio ({_codec})";

    public FFmpegAudioProcessor(string ffmpegPath, string codec)
    {
        _ffmpegPath = ffmpegPath;
        _codec = codec;
    }

    public override bool CanProcess(Job job)
        => job is AudioJob aj && aj.EncoderId == Id;

    protected override string GetExecutablePath(Job job) => _ffmpegPath;

    protected override string BuildArguments(Job job)
    {
        _stopwatch.Reset();
        var aj = (AudioJob)job;
        var s = aj.Settings;
        var args = new List<string>
        {
            "-y", "-hide_banner",
            "-i", Quote(aj.InputFile),
            "-vn",
            // Select specific audio stream
            "-map", $"0:a:{aj.AudioStreamIndex}",
            "-c:a", GetFfmpegCodecName()
        };

        // Bitrate / quality
        switch (s.BitrateMode)
        {
            case AudioBitrateMode.Cbr:
            case AudioBitrateMode.ConstrainedVbr:
                args.AddRange(["-b:a", $"{s.Bitrate}k"]);
                break;
            case AudioBitrateMode.Vbr when _codec != "flac":
                args.AddRange(["-b:a", $"{s.Bitrate}k"]);
                break;
        }

        // Sample rate
        if (s.SampleRate.HasValue)
            args.AddRange(["-ar", s.SampleRate.Value.ToString()]);

        // Channels
        var channelArg = s.ChannelMode switch
        {
            AudioChannelMode.Mono => "1",
            AudioChannelMode.Stereo => "2",
            AudioChannelMode.Surround51 => "6",
            AudioChannelMode.Surround71 => "8",
            _ => null
        };
        if (channelArg is not null)
            args.AddRange(["-ac", channelArg]);

        // Codec-specific
        args.AddRange(GetCodecSpecificArgs(s));

        // Custom args
        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        args.Add(Quote(aj.OutputFile));

        return string.Join(" ", args);
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        var sizeMatch = SizeRegex().Match(line);
        var timeMatch = TimeRegex().Match(line);

        if (sizeMatch.Success && timeMatch.Success)
        {
            long.TryParse(sizeMatch.Groups[1].Value, out var sizeKb);

            double percentage = 0;
            if (TimeSpan.TryParse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var currentTime))
            {
                var totalDuration = job is AudioJob aj && aj.TotalDurationSeconds > 0
                    ? aj.TotalDurationSeconds
                    : 0;
                if (totalDuration > 0)
                    percentage = Math.Min(100.0, currentTime.TotalSeconds / totalDuration * 100.0);
            }

            return new JobProgress
            {
                Percentage = percentage,
                OutputSizeBytes = sizeKb * 1024,
                Elapsed = _stopwatch.Elapsed,
                StatusLine = line.Trim()
            };
        }

        return null;
    }

    private string GetFfmpegCodecName() => _codec switch
    {
        "aac" => "aac",
        "opus" => "libopus",
        "mp3" => "libmp3lame",
        "flac" => "flac",
        "vorbis" => "libvorbis",
        "ac3" => "ac3",
        "eac3" => "eac3",
        _ => _codec
    };

    private string[] GetCodecSpecificArgs(AudioEncoderSettings s) => s switch
    {
        OpusSettings opus => ["-compression_level", opus.Complexity.ToString()],
        FlacSettings flac => ["-compression_level", flac.CompressionLevel.ToString()],
        AacSettings aac when aac.Profile is not null => ["-profile:a", aac.Profile],
        _ => []
    };

    [GeneratedRegex(@"size=\s*(\d+)kB")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"time=(\d{1,2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();
}
