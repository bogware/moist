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
        var args = new List<string> { "-y", "-hide_banner", "-progress", "pipe:1" };

        // Build input list with delay offsets applied BEFORE the input they affect.
        // FFmpeg requires -itsoffset to appear before the -i it modifies.
        var inputFiles = new List<string>();
        var fileIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Track which audio inputs need delay so we can apply -itsoffset before their -i
        var delayByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Collect delays for audio inputs
        foreach (var audio in mj.AudioInputs)
        {
            if (audio.DelayMs != 0)
                delayByFile[audio.FilePath] = audio.DelayMs;
        }

        // Register unique input files
        void RegisterInput(string path)
        {
            if (!fileIndexMap.ContainsKey(path))
            {
                fileIndexMap[path] = inputFiles.Count;
                inputFiles.Add(path);
            }
        }

        if (mj.VideoInput is not null)
            RegisterInput(mj.VideoInput);
        foreach (var audio in mj.AudioInputs)
            RegisterInput(audio.FilePath);
        foreach (var sub in mj.SubtitleInputs)
            RegisterInput(sub.FilePath);

        // Chapters, attachments and container tags live only in the original file — our
        // intermediate video/audio files never carry them. Add the source as an extra
        // input purely to copy those across. (It is often already registered, because
        // source subtitles are muxed from the same path.)
        var wantsSource = mj.SourceFile is not null
                          && (mj.CopyChapters || mj.CopyAttachments || mj.CopyGlobalMetadata)
                          && File.Exists(mj.SourceFile);
        if (wantsSource)
            RegisterInput(mj.SourceFile!);

        // Add -i arguments with -itsoffset before inputs that need delay
        foreach (var file in inputFiles)
        {
            if (delayByFile.TryGetValue(file, out var delayMs))
                args.AddRange(["-itsoffset", $"{delayMs}ms"]);
            args.AddRange(["-i", Quote(file)]);
        }

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
            var subsByFile = mj.SubtitleInputs.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var group in subsByFile)
            {
                var idx = fileIndexMap[group.Key];
                args.AddRange(["-map", $"{idx}:s?"]);
            }
        }

        // Attachments (subtitle fonts). Matroska only — nothing else has the concept,
        // and mapping them elsewhere fails the mux outright.
        if (wantsSource && mj.CopyAttachments && mj.Container == ContainerFormat.Mkv)
            args.AddRange(["-map", $"{fileIndexMap[mj.SourceFile!]}:t?"]);

        // Copy all streams (muxing, not re-encoding)
        args.AddRange(["-c", "copy"]);

        // Some containers (AVI, MPEG-TS) need H.264/H.265 in Annex-B form, but our
        // intermediate file is Matroska, which stores it length-prefixed. Without this
        // the mux dies with "bitstream malformed, no startcode found".
        var bsf = ContainerCompatibility.VideoBitstreamFilter(mj.Container, mj.VideoCodec);
        if (bsf is not null && mj.VideoInput is not null)
            args.AddRange(["-bsf:v", bsf]);

        // Container format
        args.AddRange(["-f", mj.Container.GetFfmpegFormat()]);

        // Per-track audio metadata. Indexed as s:a:N (Nth audio stream) rather than a
        // global stream number, which would shift depending on whether video is present.
        for (var i = 0; i < mj.AudioInputs.Count; i++)
        {
            var audio = mj.AudioInputs[i];
            if (audio.Language is not null)
                args.AddRange([$"-metadata:s:a:{i}", $"language={audio.Language}"]);
            if (audio.TrackName is not null)
                args.AddRange([$"-metadata:s:a:{i}", $"title={Quote(audio.TrackName)}"]);
            if (audio.IsDefault)
                args.AddRange([$"-disposition:a:{i}", "default"]);
        }

        // Chapters: an explicit chapter file wins, otherwise take them from the source.
        // Not every container can hold them, and asking for chapters where they aren't
        // supported is a no-op at best.
        var containerHasChapters = mj.Container is ContainerFormat.Mkv or ContainerFormat.Mp4 or ContainerFormat.Mov;

        if (mj.ChapterFile is not null)
        {
            var chapterIdx = inputFiles.Count;
            args.AddRange(["-i", Quote(mj.ChapterFile), "-map_chapters", chapterIdx.ToString()]);
        }
        else if (wantsSource && mj.CopyChapters && containerHasChapters)
        {
            args.AddRange(["-map_chapters", fileIndexMap[mj.SourceFile!].ToString()]);
        }
        else
        {
            // Otherwise be explicit: without this FFmpeg copies chapters from whichever
            // input happens to have them first.
            args.AddRange(["-map_chapters", "-1"]);
        }

        // Container-level tags (title and friends) from the source.
        if (wantsSource && mj.CopyGlobalMetadata)
            args.AddRange(["-map_metadata", fileIndexMap[mj.SourceFile!].ToString()]);

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
            TimeSpan eta = TimeSpan.Zero;
            if (timeMatch.Success &&
                TimeSpan.TryParse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var currentTime))
            {
                var totalDuration = job is MuxJob mj ? mj.TotalDurationSeconds : 0;
                if (totalDuration > 0)
                {
                    percentage = Math.Min(99.0, currentTime.TotalSeconds / totalDuration * 100.0);
                    if (currentTime.TotalSeconds > 0)
                    {
                        var rate = _stopwatch.Elapsed.TotalSeconds / currentTime.TotalSeconds;
                        var remaining = (totalDuration - currentTime.TotalSeconds) * rate;
                        eta = TimeSpan.FromSeconds(Math.Max(0, remaining));
                    }
                }
            }

            return new JobProgress
            {
                Percentage = percentage,
                OutputSizeBytes = sizeKb * 1024,
                Elapsed = _stopwatch.Elapsed,
                Eta = eta,
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
