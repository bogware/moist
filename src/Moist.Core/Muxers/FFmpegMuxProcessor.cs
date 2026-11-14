using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Moist.Core.Jobs;
using Moist.Core.Media;

namespace Moist.Core.Muxers;

/// <summary>
/// FFmpeg-based muxer. Combines video, audio, and subtitle streams into a container.
/// </summary>
public sealed partial class FFmpegMuxProcessor : CommandLineProcessor
{
    private readonly string _ffmpegPath;
    private readonly string? _doviToolPath;
    private readonly string? _hdr10PlusToolPath;
    private readonly string? _mkvmergePath;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Stopwatch _stopwatch = new();

    public override string Id => "ffmpeg-mux";
    public override string DisplayName => "FFmpeg Muxer";

    public FFmpegMuxProcessor(
        string ffmpegPath,
        string? doviToolPath = null,
        string? hdr10plusToolPath = null,
        ILoggerFactory? loggerFactory = null,
        string? mkvmergePath = null)
    {
        _ffmpegPath = ffmpegPath;
        _doviToolPath = doviToolPath;
        _hdr10PlusToolPath = hdr10plusToolPath;
        _loggerFactory = loggerFactory;
        _mkvmergePath = string.IsNullOrWhiteSpace(mkvmergePath) ? null : mkvmergePath;
    }

    public override bool CanProcess(Job job) => job is MuxJob;

    protected override string GetExecutablePath(Job job) => _ffmpegPath;

    /// <summary>
    /// Runs just before the mux command, i.e. after the video encode has produced VideoInput and
    /// before we combine streams. That is the one point where we can enrich the encoded video with
    /// the source's dynamic HDR metadata (opt-in, experimental). It replaces VideoInput in place, so
    /// the mux command built above still points at the right path. Fully guarded: the preserver
    /// never throws and never corrupts the file, and any problem is logged and skipped so the mux
    /// proceeds normally.
    /// </summary>
    protected override void OnBeforeStart(ProcessStartInfo psi, Job job)
    {
        if (job is not MuxJob { PreserveDynamicHdr: true } mj) return;
        if (!mj.SourceHasDolbyVision && !mj.SourceHasHdr10Plus) return;
        if (mj.VideoInput is null || mj.SourceFile is null) return;

        try
        {
            var preserver = new DynamicHdrPreserver(_ffmpegPath, _doviToolPath, _hdr10PlusToolPath, _loggerFactory, _mkvmergePath);
            // The mux runs on a background job worker (no sync context), so blocking here is safe.
            var result = preserver
                .PreserveAsync(mj.SourceFile, mj.VideoInput, mj.SourceHasDolbyVision, mj.SourceHasHdr10Plus)
                .GetAwaiter().GetResult();
            Logger.LogInformation("Dynamic-HDR passthrough: {Outcome} — {Message}", result.Outcome, result.Message);
        }
        catch (Exception ex)
        {
            // Belt-and-suspenders: the preserver is contracted never to throw, but never let an
            // experimental step break a normal mux.
            Logger.LogWarning(ex, "Dynamic-HDR passthrough threw unexpectedly; muxing without it.");
        }
    }

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

        // An explicit chapter file is a real input and must be registered here, among the other
        // inputs, so its -i lands BEFORE the output-side options. Appending "-i chapters" after
        // -map/-c/-f (as the old code did) bound those output options to the chapter input and
        // FFmpeg rejected -map as an input option.
        var hasChapterFile = mj.ChapterFile is not null && File.Exists(mj.ChapterFile);
        if (hasChapterFile)
            RegisterInput(mj.ChapterFile!);

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

        // Map subtitles. Each SubtitleTrackInput maps to exactly one output stream: source subs
        // by their subtitle-relative index (so a subset selection maps only the chosen streams
        // instead of every subtitle in the file), external files by their single stream. Skip
        // entirely for containers that can't carry text subtitles (AVI, MPEG-TS) — mapping them
        // there fails the whole mux.
        var subCodec = SubtitleCodecFor(mj.Container);
        var mappedSubs = new List<SubtitleTrackInput>();
        if (subCodec is not null)
        {
            foreach (var sub in mj.SubtitleInputs)
            {
                var idx = fileIndexMap[sub.FilePath];
                args.Add("-map");
                args.Add(sub.SourceStreamIndex is int si ? $"{idx}:s:{si}?" : $"{idx}:s?");
                mappedSubs.Add(sub);
            }
        }

        // Attachments (subtitle fonts). Matroska only — nothing else has the concept,
        // and mapping them elsewhere fails the mux outright.
        if (wantsSource && mj.CopyAttachments && mj.Container == ContainerFormat.Mkv)
            args.AddRange(["-map", $"{fileIndexMap[mj.SourceFile!]}:t?"]);

        // Copy all streams (muxing, not re-encoding)
        args.AddRange(["-c", "copy"]);

        // Subtitle codec must be container-aware: text subs (subrip/ass) cannot be stream-copied
        // into MP4/MOV/WebM, so FFmpeg aborts the whole mux under a blanket "-c copy". Override the
        // subtitle codec for those containers (mov_text / webvtt); Matroska copies as-is. This
        // "-c:s" overrides "-c copy" for subtitle streams only. (Image subs like PGS can't convert
        // to mov_text/webvtt; EncodeJobBuilder warns about that combination up front.)
        if (mappedSubs.Count > 0 && subCodec is not null)
            args.AddRange(["-c:s", subCodec]);

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
                args.AddRange([$"-metadata:s:a:{i}", $"title={Quote(EscapeArgValue(audio.TrackName))}"]);
            if (audio.IsDefault)
                args.AddRange([$"-disposition:a:{i}", "default"]);
        }

        // Per-subtitle metadata + disposition, indexed s:s:N by output subtitle stream. Because
        // each SubtitleTrackInput now maps to exactly one output stream, N lines up with mappedSubs.
        // For source subs this restores the language/forced flags that a subset selection would
        // otherwise lose; for an external sub it's the only way its default/forced flag is set.
        for (var n = 0; n < mappedSubs.Count; n++)
        {
            var sub = mappedSubs[n];
            if (sub.Language is not null)
                args.AddRange([$"-metadata:s:s:{n}", $"language={sub.Language}"]);
            if (sub.TrackName is not null)
                args.AddRange([$"-metadata:s:s:{n}", $"title={Quote(EscapeArgValue(sub.TrackName))}"]);
            var disposition = SubtitleDisposition(sub.IsDefault, sub.IsForced);
            if (disposition is not null)
                args.AddRange([$"-disposition:s:{n}", disposition]);
        }

        // Chapters: an explicit chapter file wins, otherwise take them from the source.
        // Not every container can hold them, and asking for chapters where they aren't
        // supported is a no-op at best.
        var containerHasChapters = mj.Container is ContainerFormat.Mkv or ContainerFormat.Mp4 or ContainerFormat.Mov;

        if (hasChapterFile)
        {
            // The chapter file was registered as a normal input above; reference it by index.
            // Its streams are never -map'd, so -c copy is a no-op for it — only its chapters
            // are taken via -map_chapters.
            args.AddRange(["-map_chapters", fileIndexMap[mj.ChapterFile!].ToString()]);
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

    /// <summary>
    /// The subtitle codec to use for a container, or null when the container can't carry
    /// (text) subtitles. Matroska copies srt/ass/pgs as-is; MP4/MOV need mov_text and WebM
    /// needs webvtt because text subs can't be stream-copied into them; AVI and MPEG-TS get
    /// no subtitle streams at all.
    /// </summary>
    private static string? SubtitleCodecFor(ContainerFormat container) => container switch
    {
        ContainerFormat.Mkv => "copy",
        ContainerFormat.Mp4 or ContainerFormat.Mov => "mov_text",
        ContainerFormat.Webm => "webvtt",
        _ => null,
    };

    /// <summary>FFmpeg -disposition value for a subtitle, or null when neither flag is set.</summary>
    private static string? SubtitleDisposition(bool isDefault, bool isForced) => (isDefault, isForced) switch
    {
        (true, true) => "default+forced",
        (true, false) => "default",
        (false, true) => "forced",
        _ => null,
    };

    /// <summary>
    /// Escape a metadata value that will be wrapped in double quotes and passed through
    /// ProcessStartInfo.Arguments (which is re-parsed by the OS). Track titles come from source
    /// metadata, so a title containing a quote would otherwise corrupt the argument split. Only
    /// for values, never for paths — <see cref="CommandLineProcessor.Quote"/> keeps paths intact.
    /// </summary>
    private static string EscapeArgValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [GeneratedRegex(@"size=\s*(\d+)kB")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"time=(\d{1,2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex TimeRegex();
}
