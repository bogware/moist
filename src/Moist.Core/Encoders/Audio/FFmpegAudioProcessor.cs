using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Encoders.Audio;

/// <summary>
/// FFmpeg-based audio encoder processor. Supports AAC, Opus, FLAC, MP3, etc.
/// </summary>
public partial class FFmpegAudioProcessor : CommandLineProcessor
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
        var args = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };

        // A/V alignment: when the audio stream began before the first video frame, drop that
        // lead so this track starts at the same instant the video encode does. Input-side -ss
        // is sample-accurate for audio and costs nothing.
        if (aj.TrimStart is { } trimStart && trimStart > TimeSpan.Zero)
            args.AddRange(["-ss", trimStart.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);

        args.AddRange(
        [
            "-i", Quote(aj.InputFile),
            "-vn",
            // Select specific audio stream
            "-map", $"0:a:{aj.AudioStreamIndex}",
            "-c:a", GetFfmpegCodecName(),
        ]);

        // Passthrough mode: skip all encoding options
        if (_codec == "copy")
        {
            args.Add(Quote(aj.OutputFile));
            return string.Join(" ", args);
        }

        // Bitrate / quality
        switch (s.BitrateMode)
        {
            case AudioBitrateMode.Cbr:
            case AudioBitrateMode.ConstrainedVbr:
                args.AddRange(["-b:a", $"{s.Bitrate}k"]);
                break;
            // FLAC is lossless (bitrate ignored); MP3/Vorbis take the -q:a quality-scale
            // path in VBR (see GetCodecSpecificArgs), where a co-emitted -b:a is silently
            // dropped by libmp3lame/libvorbis — so don't send it and muddy the command.
            case AudioBitrateMode.Vbr when _codec != "flac" && _codec != "mp3" && _codec != "vorbis":
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

        // Sample format. FFmpeg has no "s24" sample format (-sample_fmts lists only
        // u8/s16/s32/flt/dbl and their planar/s64 variants), so 24-bit audio is carried
        // in the s32 container; passing "s24" aborts the encode with "Invalid sample format".
        var sampleFmtArg = s.SampleFormat switch
        {
            AudioSampleFormat.S16 => "s16",
            AudioSampleFormat.S24 => "s32",
            AudioSampleFormat.S32 => "s32",
            AudioSampleFormat.Float => "flt",
            _ => null
        };
        if (sampleFmtArg is not null)
            args.AddRange(["-sample_fmt", sampleFmtArg]);

        // For true 24-bit FLAC (rather than 32-bit-padded), tell the encoder the real depth.
        if (s.SampleFormat == AudioSampleFormat.S24 && _codec == "flac")
            args.AddRange(["-bits_per_raw_sample", "24"]);

        // Normalization via audio filter chain
        var normFilter = BuildNormalizationFilter(s);
        if (normFilter is not null)
            args.AddRange(["-af", normFilter]);

        // Codec-specific
        args.AddRange(GetCodecSpecificArgs(s));

        // Custom args
        if (!string.IsNullOrWhiteSpace(s.CustomArgs))
            args.Add(s.CustomArgs);

        args.Add(Quote(aj.OutputFile));

        return string.Join(" ", args);
    }

    private static string? BuildNormalizationFilter(AudioEncoderSettings s)
    {
        return s.Normalization switch
        {
            AudioNormalizationMode.EbuR128 =>
                $"loudnorm=I={Fmt(s.NormalizationTarget)}:TP={Fmt(s.TruePeakLimit)}:LRA=11:print_format=summary",
            AudioNormalizationMode.Peak =>
                $"acompressor=threshold={Fmt(s.TruePeakLimit)}dB:ratio=20:attack=0.1:release=50,volume={Fmt(s.NormalizationTarget)}dB",
            AudioNormalizationMode.Rms =>
                $"dynaudnorm=f=150:g=15:p={Fmt(Math.Clamp(Math.Pow(10, s.NormalizationTarget / 20.0), 0.01, 1.0))}:m=10",
            _ => null
        };
    }

    private static string Fmt(double v) => v.ToString("G", CultureInfo.InvariantCulture);

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
        "copy" => "copy",
        _ => _codec
    };

    private List<string> GetCodecSpecificArgs(AudioEncoderSettings s)
    {
        var codecArgs = new List<string>();
        switch (s)
        {
            case OpusSettings opus:
                codecArgs.AddRange(["-compression_level", opus.Complexity.ToString()]);
                // libopus defaults to VBR; without an explicit -vbr the Cbr/ConstrainedVbr
                // requests are silently ignored. -vbr is libopus-specific, so it stays here
                // rather than in the shared bitrate switch (aac/mp3/flac/... reject it).
                codecArgs.AddRange(["-vbr", opus.BitrateMode switch
                {
                    AudioBitrateMode.Cbr => "off",
                    AudioBitrateMode.ConstrainedVbr => "constrained",
                    _ => "on"
                }]);
                break;

            case FlacSettings flac:
                codecArgs.AddRange(["-compression_level", flac.CompressionLevel.ToString()]);
                break;

            case AacSettings aac:
                if (aac.Profile is not null)
                    codecArgs.AddRange(["-profile:a", aac.Profile]);
                break;

            case Mp3Settings mp3:
                if (mp3.BitrateMode == AudioBitrateMode.Vbr)
                    codecArgs.AddRange(["-q:a", mp3.VbrScale.ToString()]);
                // libmp3lame: -joint_stereo 1 enables joint/mid-side stereo, 0 forces simple stereo
                switch (mp3.Mp3ChannelMode)
                {
                    case Mp3ChannelMode.JointStereo:
                    case Mp3ChannelMode.ForcedJointStereo:
                        codecArgs.AddRange(["-joint_stereo", "1"]);
                        break;
                    case Mp3ChannelMode.Stereo:
                        codecArgs.AddRange(["-joint_stereo", "0"]);
                        break;
                    case Mp3ChannelMode.DualMono:
                        codecArgs.AddRange(["-ac", "1"]);
                        break;
                }
                break;

            case Ac3Settings ac3:
                if (ac3.DialogueNormalization > -31)
                    codecArgs.AddRange(["-dialnorm", ac3.DialogueNormalization.ToString()]);
                if (ac3.SurroundMode != Ac3SurroundMode.NotIndicated)
                    codecArgs.AddRange(["-dsur_mode", ((int)ac3.SurroundMode).ToString()]);
                break;

            case Eac3Settings eac3:
                if (eac3.DialogueNormalization > -31)
                    codecArgs.AddRange(["-dialnorm", eac3.DialogueNormalization.ToString()]);
                if (eac3.MixingLevel.HasValue)
                    codecArgs.AddRange(["-mixing_level", eac3.MixingLevel.Value.ToString()]);
                break;

            case VorbisSettings vorbis:
                if (vorbis.BitrateMode == AudioBitrateMode.Vbr)
                    codecArgs.AddRange(["-q:a", vorbis.QualityScale.ToString()]);
                break;
        }
        return codecArgs;
    }

    [GeneratedRegex(@"size=\s*(\d+)kB")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"time=(\d{1,2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();
}
