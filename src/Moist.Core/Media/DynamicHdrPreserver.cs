using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Media;

/// <summary>
/// <para>
/// <b>EXPERIMENTAL and UNVERIFIED.</b> Preserves dynamic HDR metadata — a Dolby Vision RPU and/or
/// HDR10+ dynamic metadata — across a re-encode by orchestrating the external <c>dovi_tool</c> and
/// <c>hdr10plus_tool</c> CLIs over FFmpeg. The static HDR path (transfer/primaries/mastering-display,
/// content-light-level) survives a normal re-encode; the <em>dynamic</em> metadata does not, and this
/// service extracts it from the source elementary stream and injects it into the freshly encoded one.
/// </para>
/// <para>
/// This code has never been run against real Dolby Vision / HDR10+ content or the real tools on the
/// development machine (see the "Known gap: HDR10+ / Dolby Vision passthrough" note in the project
/// guide). It is pure CLI orchestration, deliberately <b>not wired into the job pipeline</b> — the
/// caller decides whether to invoke it. Treat a <see cref="DynamicHdrOutcome.Preserved"/> result as a
/// claim to verify, not a guarantee.
/// </para>
/// <para>
/// <b>Safety contract.</b> <see cref="PreserveAsync"/> never throws and never mutates or deletes the
/// input on any failure: it works entirely in a scratch area and only replaces the encoded file via a
/// single atomic same-directory move once every step has succeeded. Any missing tool, non-zero exit,
/// or exception logs a warning and returns <see cref="DynamicHdrOutcome.ToolMissing"/> /
/// <see cref="DynamicHdrOutcome.Failed"/> with the encoded file left exactly as it was.
/// </para>
/// </summary>
public sealed class DynamicHdrPreserver
{
    private readonly string _ffmpegPath;
    private readonly string? _doviToolPath;
    private readonly string? _hdr10PlusToolPath;
    private readonly ILogger _logger;

    /// <summary>Raw HEVC elementary-stream extensions — an encoded file of this kind needs no remux.</summary>
    private static readonly string[] RawHevcExtensions = [".hevc", ".h265", ".265"];

    /// <summary>
    /// Creates a preserver bound to the given tool paths. A null/blank tool path means "not
    /// configured", which surfaces as <see cref="DynamicHdrOutcome.ToolMissing"/> only when that tool
    /// is actually required by the requested passthrough.
    /// </summary>
    /// <param name="ffmpegPath">Path to (or bare name of) the ffmpeg binary; used to demux and remux.</param>
    /// <param name="doviToolPath">Path to <c>dovi_tool</c>, or null when Dolby Vision is not needed.</param>
    /// <param name="hdr10plusToolPath">Path to <c>hdr10plus_tool</c>, or null when HDR10+ is not needed.</param>
    /// <param name="loggerFactory">Optional logger factory; falls back to a null logger.</param>
    public DynamicHdrPreserver(
        string ffmpegPath,
        string? doviToolPath,
        string? hdr10plusToolPath,
        ILoggerFactory? loggerFactory = null)
    {
        _ffmpegPath = ffmpegPath;
        _doviToolPath = doviToolPath;
        _hdr10PlusToolPath = hdr10plusToolPath;
        _logger = loggerFactory?.CreateLogger<DynamicHdrPreserver>()
                  ?? NullLogger<DynamicHdrPreserver>.Instance;
    }

    /// <summary>
    /// <para>
    /// Attempts to carry the source's dynamic HDR metadata into <paramref name="encodedHevcOrMkv"/>,
    /// replacing that file in place on success. <b>EXPERIMENTAL</b> — see the type-level remarks.
    /// </para>
    /// <para>
    /// The steps, all in a scratch area: demux an Annex-B HEVC elementary stream from the source and
    /// from the encoded file; <c>dovi_tool extract-rpu</c> / <c>hdr10plus_tool extract</c> from the
    /// source stream; <c>inject-rpu</c> / <c>inject</c> into the encoded stream; then remux the
    /// injected stream back into the encoded file's container (audio, subtitles, chapters and metadata
    /// copied from the original encoded file). The original encoded file is only touched by a final
    /// atomic move once all of that succeeds.
    /// </para>
    /// </summary>
    /// <param name="sourceFile">The original source carrying the dynamic HDR metadata.</param>
    /// <param name="encodedHevcOrMkv">The freshly encoded file to enrich; a raw HEVC stream or a container (MKV/MP4/...).</param>
    /// <param name="hasDolbyVision">Preserve the Dolby Vision RPU (typically <see cref="VideoStreamInfo.HasDolbyVision"/>).</param>
    /// <param name="hasHdr10Plus">Preserve HDR10+ metadata (typically <see cref="VideoStreamInfo.HasHdr10Plus"/>).</param>
    /// <param name="ct">Cancellation token. Cancellation is reported as <see cref="DynamicHdrOutcome.Failed"/>; this method never throws.</param>
    /// <returns>A <see cref="DynamicHdrResult"/> describing the outcome. The encoded file is unchanged unless the outcome is <see cref="DynamicHdrOutcome.Preserved"/>.</returns>
    public async Task<DynamicHdrResult> PreserveAsync(
        string sourceFile,
        string encodedHevcOrMkv,
        bool hasDolbyVision,
        bool hasHdr10Plus,
        CancellationToken ct = default)
    {
        if (!hasDolbyVision && !hasHdr10Plus)
            return DynamicHdrResult.Skipped("No dynamic HDR metadata to preserve.");

        if (!File.Exists(sourceFile))
            return DynamicHdrResult.Failed($"Source file not found: {sourceFile}");
        if (!File.Exists(encodedHevcOrMkv))
            return DynamicHdrResult.Failed($"Encoded file not found: {encodedHevcOrMkv}");

        // Tool availability up front — never start any work if a required tool is unconfigured.
        if (hasDolbyVision && string.IsNullOrWhiteSpace(_doviToolPath))
        {
            _logger.LogWarning(
                "Experimental HDR passthrough: dovi_tool is not configured; leaving {File} untouched.",
                encodedHevcOrMkv);
            return DynamicHdrResult.ToolMissing(
                "dovi_tool is not configured; the Dolby Vision RPU was not preserved.");
        }
        if (hasHdr10Plus && string.IsNullOrWhiteSpace(_hdr10PlusToolPath))
        {
            _logger.LogWarning(
                "Experimental HDR passthrough: hdr10plus_tool is not configured; leaving {File} untouched.",
                encodedHevcOrMkv);
            return DynamicHdrResult.ToolMissing(
                "hdr10plus_tool is not configured; HDR10+ metadata was not preserved.");
        }

        // Scratch area for the elementary streams and metadata. Best-effort cleanup in finally.
        var scratch = Path.Combine(Path.GetTempPath(), $"moist_hdr_{Guid.NewGuid():N}");

        // The in-place replacement is staged next to the encoded file so the final move is a
        // same-volume rename rather than a cross-volume copy that could be interrupted mid-write.
        var ext = Path.GetExtension(encodedHevcOrMkv);
        var replacement = encodedHevcOrMkv + ".hdrpass.tmp" + ext;

        try
        {
            Directory.CreateDirectory(scratch);

            var sourceHevc = Path.Combine(scratch, "source.hevc");
            var encodedHevc = Path.Combine(scratch, "encoded.hevc");

            // 1) Demux Annex-B HEVC from both inputs (ffmpeg handles raw HEVC and containers alike).
            if (!await RunFfmpegAsync(BuildDemuxHevcArgs(sourceFile, sourceHevc), "demux source HEVC", ct))
                return Failed(encodedHevcOrMkv, "failed to demux the HEVC stream from the source.");
            if (!IsNonEmptyFile(sourceHevc))
                return Failed(encodedHevcOrMkv, "the demuxed source HEVC stream was empty.");

            if (!await RunFfmpegAsync(BuildDemuxHevcArgs(encodedHevcOrMkv, encodedHevc), "demux encoded HEVC", ct))
                return Failed(encodedHevcOrMkv, "failed to demux the HEVC stream from the encoded file.");
            if (!IsNonEmptyFile(encodedHevc))
                return Failed(encodedHevcOrMkv, "the demuxed encoded HEVC stream was empty.");

            // 2) Inject the metadata into the encoded stream, chaining one tool's output into the
            //    next so a file carrying both DV and HDR10+ keeps both.
            var currentHevc = encodedHevc;

            if (hasDolbyVision)
            {
                var rpu = Path.Combine(scratch, "rpu.bin");
                var extract = await RunToolAsync(_doviToolPath!, BuildExtractRpuArgs(sourceHevc, rpu), "dovi_tool extract-rpu", ct);
                if (extract != ToolRunOutcome.Ok)
                    return extract == ToolRunOutcome.Missing
                        ? ToolMissing(encodedHevcOrMkv, "dovi_tool", "could not be launched")
                        : Failed(encodedHevcOrMkv, "dovi_tool extract-rpu failed.");
                if (!IsNonEmptyFile(rpu))
                    return Failed(encodedHevcOrMkv, "dovi_tool produced no RPU.");

                var injected = Path.Combine(scratch, "encoded.dv.hevc");
                var inject = await RunToolAsync(_doviToolPath!, BuildInjectRpuArgs(currentHevc, rpu, injected), "dovi_tool inject-rpu", ct);
                if (inject != ToolRunOutcome.Ok)
                    return inject == ToolRunOutcome.Missing
                        ? ToolMissing(encodedHevcOrMkv, "dovi_tool", "could not be launched")
                        : Failed(encodedHevcOrMkv, "dovi_tool inject-rpu failed.");
                if (!IsNonEmptyFile(injected))
                    return Failed(encodedHevcOrMkv, "dovi_tool inject-rpu produced no output.");

                currentHevc = injected;
            }

            if (hasHdr10Plus)
            {
                var json = Path.Combine(scratch, "hdr10plus.json");
                var extract = await RunToolAsync(_hdr10PlusToolPath!, BuildHdr10PlusExtractArgs(sourceHevc, json), "hdr10plus_tool extract", ct);
                if (extract != ToolRunOutcome.Ok)
                    return extract == ToolRunOutcome.Missing
                        ? ToolMissing(encodedHevcOrMkv, "hdr10plus_tool", "could not be launched")
                        : Failed(encodedHevcOrMkv, "hdr10plus_tool extract failed.");
                if (!IsNonEmptyFile(json))
                    return Failed(encodedHevcOrMkv, "hdr10plus_tool produced no metadata.");

                var injected = Path.Combine(scratch, "encoded.hdr10plus.hevc");
                var inject = await RunToolAsync(_hdr10PlusToolPath!, BuildHdr10PlusInjectArgs(currentHevc, json, injected), "hdr10plus_tool inject", ct);
                if (inject != ToolRunOutcome.Ok)
                    return inject == ToolRunOutcome.Missing
                        ? ToolMissing(encodedHevcOrMkv, "hdr10plus_tool", "could not be launched")
                        : Failed(encodedHevcOrMkv, "hdr10plus_tool inject failed.");
                if (!IsNonEmptyFile(injected))
                    return Failed(encodedHevcOrMkv, "hdr10plus_tool inject produced no output.");

                currentHevc = injected;
            }

            // 3) Produce the replacement. A raw-HEVC encoded target takes the injected stream
            //    directly; a container is rebuilt with the injected video plus the original file's
            //    audio/subtitles/chapters/metadata.
            if (IsRawHevc(ext))
            {
                File.Copy(currentHevc, replacement, overwrite: true);
            }
            else
            {
                if (!await RunFfmpegAsync(BuildRemuxArgs(currentHevc, encodedHevcOrMkv, replacement), "remux", ct))
                    return Failed(encodedHevcOrMkv, "failed to remux the enriched stream back into the container.");
            }

            if (!IsNonEmptyFile(replacement))
                return Failed(encodedHevcOrMkv, "the enriched output was empty; the encoded file was left untouched.");

            // 4) The single mutation: atomically swap the replacement in for the encoded file.
            File.Move(replacement, encodedHevcOrMkv, overwrite: true);

            var what = (hasDolbyVision, hasHdr10Plus) switch
            {
                (true, true) => "Dolby Vision RPU and HDR10+ metadata",
                (true, false) => "Dolby Vision RPU",
                _ => "HDR10+ metadata",
            };
            _logger.LogInformation(
                "Experimental HDR passthrough: preserved {What} into {File}. UNVERIFIED — validate the result.",
                what, encodedHevcOrMkv);
            return DynamicHdrResult.Preserved($"Preserved {what} (experimental — verify the result).");
        }
        catch (OperationCanceledException)
        {
            // The safety contract forbids throwing; report cancellation as a non-fatal failure.
            _logger.LogWarning(
                "Experimental HDR passthrough was cancelled; {File} was left untouched.", encodedHevcOrMkv);
            return DynamicHdrResult.Failed("Cancelled; the encoded file was left untouched.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Experimental HDR passthrough failed; {File} was left untouched.", encodedHevcOrMkv);
            return DynamicHdrResult.Failed($"Unexpected error: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(replacement);
            TryDeleteDirectory(scratch);
        }
    }

    // ---- Argument builders (internal static so they can be unit-tested without the tools present) ----

    /// <summary>
    /// ffmpeg args to copy the first video stream out as an Annex-B HEVC elementary stream. Works for
    /// both raw HEVC and container inputs; <c>hevc_mp4toannexb</c> is a no-op on already-Annex-B data.
    /// </summary>
    internal static IReadOnlyList<string> BuildDemuxHevcArgs(string inputFile, string outputHevcPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", inputFile,
        "-map", "0:v:0",
        "-c:v", "copy",
        "-bsf:v", "hevc_mp4toannexb",
        "-f", "hevc",
        outputHevcPath,
    ];

    /// <summary>dovi_tool args to extract the Dolby Vision RPU from an HEVC elementary stream.</summary>
    internal static IReadOnlyList<string> BuildExtractRpuArgs(string sourceHevcPath, string rpuOutPath) =>
    [
        "extract-rpu", "-i", sourceHevcPath, "-o", rpuOutPath,
    ];

    /// <summary>dovi_tool args to inject a previously extracted RPU into an HEVC elementary stream.</summary>
    internal static IReadOnlyList<string> BuildInjectRpuArgs(string encodedHevcPath, string rpuInPath, string outHevcPath) =>
    [
        "inject-rpu", "-i", encodedHevcPath, "--rpu-in", rpuInPath, "-o", outHevcPath,
    ];

    /// <summary>hdr10plus_tool args to extract HDR10+ dynamic metadata to JSON.</summary>
    internal static IReadOnlyList<string> BuildHdr10PlusExtractArgs(string sourceHevcPath, string jsonOutPath) =>
    [
        "extract", "-i", sourceHevcPath, "-o", jsonOutPath,
    ];

    /// <summary>hdr10plus_tool args to inject HDR10+ JSON metadata into an HEVC elementary stream.</summary>
    internal static IReadOnlyList<string> BuildHdr10PlusInjectArgs(string encodedHevcPath, string jsonInPath, string outHevcPath) =>
    [
        "inject", "-i", encodedHevcPath, "-j", jsonInPath, "-o", outHevcPath,
    ];

    /// <summary>
    /// ffmpeg args to rebuild a container from the enriched HEVC video plus everything else
    /// (audio, subtitles, chapters, metadata) copied from the original encoded file. <c>-map 1</c>
    /// followed by <c>-map -1:v</c> takes every stream of the original except its video.
    /// </summary>
    internal static IReadOnlyList<string> BuildRemuxArgs(string injectedHevcPath, string originalEncodedPath, string outContainerPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", injectedHevcPath,
        "-i", originalEncodedPath,
        "-map", "0:v:0",
        "-map", "1",
        "-map", "-1:v",
        "-c", "copy",
        "-map_metadata", "1",
        "-map_chapters", "1",
        outContainerPath,
    ];

    // ---- Process orchestration ----

    private Task<bool> RunFfmpegAsync(IReadOnlyList<string> args, string step, CancellationToken ct) =>
        RunFfmpegOrToolAsync(_ffmpegPath, args, step, ct);

    private async Task<bool> RunFfmpegOrToolAsync(
        string exe, IReadOnlyList<string> args, string step, CancellationToken ct)
    {
        var (started, exitCode) = await RunAsync(exe, args, ct);
        if (!started)
        {
            _logger.LogWarning("Experimental HDR passthrough: could not launch ffmpeg for {Step}.", step);
            return false;
        }
        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Experimental HDR passthrough: {Step} exited with code {Code}.", step, exitCode);
            return false;
        }
        return true;
    }

    /// <summary>Outcome of one external-tool invocation.</summary>
    private enum ToolRunOutcome
    {
        /// <summary>The tool ran and exited zero.</summary>
        Ok,

        /// <summary>The binary could not be launched (not found on PATH / bad path).</summary>
        Missing,

        /// <summary>The tool ran but exited non-zero.</summary>
        Failed,
    }

    /// <summary>
    /// Runs an external tool, distinguishing a launch failure (binary not found →
    /// <see cref="ToolRunOutcome.Missing"/>) from a genuine non-zero exit
    /// (<see cref="ToolRunOutcome.Failed"/>). Async methods cannot take <c>out</c> parameters, hence
    /// the enum return.
    /// </summary>
    private async Task<ToolRunOutcome> RunToolAsync(
        string exe, IReadOnlyList<string> args, string step, CancellationToken ct)
    {
        var (started, exitCode) = await RunAsync(exe, args, ct);
        if (!started)
        {
            _logger.LogWarning("Experimental HDR passthrough: could not launch {Step}.", step);
            return ToolRunOutcome.Missing;
        }
        if (exitCode != 0)
        {
            _logger.LogWarning("Experimental HDR passthrough: {Step} exited with code {Code}.", step, exitCode);
            return ToolRunOutcome.Failed;
        }
        return ToolRunOutcome.Ok;
    }

    /// <summary>
    /// Starts a process with <see cref="ProcessStartInfo.ArgumentList"/> (no shell re-parsing),
    /// drains both pipes so a chatty tool cannot deadlock, and kills the child on cancellation.
    /// Returns whether the process started and, if so, its exit code. Never throws for a normal
    /// "binary not found" — that surfaces as <c>started == false</c>.
    /// </summary>
    private static async Task<(bool Started, int ExitCode)> RunAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // Binary not on PATH / not found — treated as "tool missing" by the caller.
            return (false, -1);
        }

        if (process is null)
            return (false, -1);

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { /* pipe drain best-effort */ }
            return (true, process.ExitCode);
        }
    }

    // ---- Small helpers ----

    private DynamicHdrResult Failed(string encodedFile, string reason)
    {
        _logger.LogWarning(
            "Experimental HDR passthrough: {Reason} {File} was left untouched.", reason, encodedFile);
        return DynamicHdrResult.Failed(reason);
    }

    private DynamicHdrResult ToolMissing(string encodedFile, string tool, string detail)
    {
        _logger.LogWarning(
            "Experimental HDR passthrough: {Tool} {Detail}; {File} was left untouched.",
            tool, detail, encodedFile);
        return DynamicHdrResult.ToolMissing($"{tool} {detail}; the metadata was not preserved.");
    }

    private static bool IsRawHevc(string extension) =>
        RawHevcExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    private static bool IsNonEmptyFile(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
