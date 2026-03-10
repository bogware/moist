using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Moist.Core.Jobs;

namespace Moist.Core.Muxers;

/// <summary>
/// FFmpeg-based muxer. Combines video, audio, and subtitle streams into a container.
/// </summary>
public sealed partial class FFmpegMuxProcessor : CommandLineProcessor
{
    private readonly string _ffmpegPath;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => "ffmpeg-mux";
    public override string DisplayName => "FFmpeg Muxer";

    public FFmpegMuxProcessor(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    public override bool CanProcess(Job job) => job is MuxJob;

    protected override string GetExecutablePath(Job job) => _ffmpegPath;

    protected override string BuildArguments(Job job)
    {
        _stopwatch.Reset();
        var mj = (MuxJob)job;
        var args = new List<string> { "-y", "-hide_banner" };

        // Collect unique input files and track their indices
        var inputFiles = new List<string>();
        var fileIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddInput(string path)
        {
            if (!fileIndexMap.ContainsKey(path))
            {
                fileIndexMap[path] = inputFiles.Count;
                inputFiles.Add(path);
            }
        }

        // Register all input files (deduplicated)
        if (mj.VideoInput is not null)
            AddInput(mj.VideoInput);
        foreach (var audio in mj.AudioInputs)
            AddInput(audio.FilePath);
        foreach (var sub in mj.SubtitleInputs)
            AddInput(sub.FilePath);

        // Add -i arguments
        foreach (var file in inputFiles)
            args.AddRange(["-i", Quote(file)]);

        // Map video
        if (mj.VideoInput is not null)
            args.AddRange(["-map", $"{fileIndexMap[mj.VideoInput]}:v"]);

        // Map audio
        foreach (var audio in mj.AudioInputs)
        {
            var idx = fileIndexMap[audio.FilePath];
            args.AddRange(["-map", $"{idx}:a"]);
        }

        // Map subtitles — if they come from the source file, map each stream
        if (mj.SubtitleInputs.Count > 0)
        {
            // Group by file to handle "copy all subs from source" efficiently
            var subsByFile = mj.SubtitleInputs.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var group in subsByFile)
            {
                var idx = fileIndexMap[group.Key];
                // If the source is also the main input and has multiple subs, map all subtitle streams
                args.AddRange(["-map", $"{idx}:s?"]);
            }
        }

        // Copy all streams (muxing, not re-encoding)
        args.AddRange(["-c", "copy"]);

        // Container format
        args.AddRange(["-f", mj.Container.GetFfmpegFormat()]);

        // Metadata for audio tracks
        var streamIdx = mj.VideoInput is not null ? 1 : 0;
        foreach (var audio in mj.AudioInputs)
        {
            if (audio.Language is not null)
                args.AddRange([$"-metadata:s:{streamIdx}", $"language={audio.Language}"]);
            if (audio.TrackName is not null)
                args.AddRange([$"-metadata:s:{streamIdx}", $"title={audio.TrackName}"]);
            if (audio.DelayMs != 0)
                args.AddRange([$"-itsoffset", $"{audio.DelayMs}ms"]);
            streamIdx++;
        }

        // Chapter file
        if (mj.ChapterFile is not null)
        {
            var chapterIdx = inputFiles.Count;
            args.AddRange(["-i", Quote(mj.ChapterFile), "-map_chapters", chapterIdx.ToString()]);
        }

        args.Add(Quote(mj.OutputFile));

        return string.Join(" ", args);
    }

    protected override JobProgress? ParseOutputLine(string line, Job job)
    {
        if (!_stopwatch.IsRunning) _stopwatch.Start();

        var sizeMatch = SizeRegex().Match(line);
        var timeMatch = TimeRegex().Match(line);

        if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out var sizeKb))
        {
            double percentage = 0;
            if (timeMatch.Success &&
                TimeSpan.TryParse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var currentTime))
            {
                // Estimate total duration from source file if available
                var mj = job as MuxJob;
                // Mux duration should roughly match source — use elapsed/speed heuristic
                percentage = Math.Min(99.0, currentTime.TotalSeconds > 0 ? currentTime.TotalSeconds : 0);
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

    [GeneratedRegex(@"size=\s*(\d+)kB")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"time=(\d{1,2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();
}
