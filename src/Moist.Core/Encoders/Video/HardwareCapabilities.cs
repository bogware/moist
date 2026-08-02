using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moist.Core.Encoders.Video;

/// <summary>One GPU encoder moist knows how to drive.</summary>
public sealed class HardwareEncoderDefinition
{
    public required string Id { get; init; }
    public required string FfmpegName { get; init; }
    public required HardwareVendor Vendor { get; init; }

    /// <summary>Canonical stream codec: h264, hevc, av1 or vp9.</summary>
    public required string Codec { get; init; }

    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// True when this vendor's encode path has never been exercised on real silicon.
    /// The arguments follow the documented interface and detection correctly reports
    /// the hardware as absent, but "correctly detected as missing" is not the same as
    /// "known to produce a good file". Surfaced in the UI so nobody is misled.
    /// </summary>
    public bool Experimental => Vendor is not HardwareVendor.Nvenc;

    /// <summary>Name as shown to users, flagged when unverified.</summary>
    public string Label => Experimental ? $"{DisplayName} [experimental]" : DisplayName;
}

/// <summary>Whether an encoder is actually usable, and why not if it isn't.</summary>
public sealed class HardwareEncoderStatus
{
    public required HardwareEncoderDefinition Encoder { get; init; }

    /// <summary>The FFmpeg build lists this encoder.</summary>
    public bool CompiledIn { get; init; }

    /// <summary>A real encode succeeded — the hardware is present and working.</summary>
    public bool Usable { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// Works out which GPU encoders can actually be used on this machine.
///
/// Listing is not proof. <c>ffmpeg -encoders</c> reports everything the binary was
/// compiled with, so a stock Windows build advertises NVENC, QSV, AMF and VA-API
/// regardless of what silicon is installed. The only reliable test is to encode a few
/// frames and see whether it works, which is what <see cref="DetectAsync"/> does.
/// </summary>
public sealed class HardwareCapabilities(ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger<HardwareCapabilities>() ?? NullLogger<HardwareCapabilities>.Instance;

    private IReadOnlyList<HardwareEncoderStatus>? _cached;

    public static IReadOnlyList<HardwareEncoderDefinition> Known { get; } =
    [
        // NVIDIA
        New("hw-nvenc-h264", "h264_nvenc", HardwareVendor.Nvenc, "h264", "H.264 (NVIDIA NVENC)", "GPU H.264 on NVIDIA. Much faster than x264, larger files at the same quality."),
        New("hw-nvenc-hevc", "hevc_nvenc", HardwareVendor.Nvenc, "hevc", "H.265 (NVIDIA NVENC)", "GPU H.265 on NVIDIA. Needs Maxwell 2nd gen or newer."),
        New("hw-nvenc-av1", "av1_nvenc", HardwareVendor.Nvenc, "av1", "AV1 (NVIDIA NVENC)", "GPU AV1 on NVIDIA. Needs RTX 40 series or newer."),

        // Intel
        New("hw-qsv-h264", "h264_qsv", HardwareVendor.Qsv, "h264", "H.264 (Intel Quick Sync)", "GPU H.264 on Intel integrated or Arc graphics."),
        New("hw-qsv-hevc", "hevc_qsv", HardwareVendor.Qsv, "hevc", "H.265 (Intel Quick Sync)", "GPU H.265 on Intel."),
        New("hw-qsv-av1", "av1_qsv", HardwareVendor.Qsv, "av1", "AV1 (Intel Quick Sync)", "GPU AV1 on Intel. Needs Arc or 12th gen or newer."),
        New("hw-qsv-vp9", "vp9_qsv", HardwareVendor.Qsv, "vp9", "VP9 (Intel Quick Sync)", "GPU VP9 on Intel."),

        // AMD
        New("hw-amf-h264", "h264_amf", HardwareVendor.Amf, "h264", "H.264 (AMD AMF)", "GPU H.264 on AMD. Compression efficiency trails NVENC and Quick Sync."),
        New("hw-amf-hevc", "hevc_amf", HardwareVendor.Amf, "hevc", "H.265 (AMD AMF)", "GPU H.265 on AMD."),
        New("hw-amf-av1", "av1_amf", HardwareVendor.Amf, "av1", "AV1 (AMD AMF)", "GPU AV1 on AMD. Needs RX 7000 series or newer."),

        // Apple
        New("hw-vt-h264", "h264_videotoolbox", HardwareVendor.VideoToolbox, "h264", "H.264 (Apple VideoToolbox)", "Apple Silicon and Intel Mac media engine."),
        New("hw-vt-hevc", "hevc_videotoolbox", HardwareVendor.VideoToolbox, "hevc", "H.265 (Apple VideoToolbox)", "Apple media engine."),

        // Linux VA-API
        New("hw-vaapi-h264", "h264_vaapi", HardwareVendor.Vaapi, "h264", "H.264 (VA-API)", "Linux GPU encoding through VA-API."),
        New("hw-vaapi-hevc", "hevc_vaapi", HardwareVendor.Vaapi, "hevc", "H.265 (VA-API)", "Linux GPU encoding through VA-API."),
        New("hw-vaapi-av1", "av1_vaapi", HardwareVendor.Vaapi, "av1", "AV1 (VA-API)", "Linux GPU AV1 through VA-API."),
    ];

    private static HardwareEncoderDefinition New(
        string id, string ffmpegName, HardwareVendor vendor, string codec, string displayName, string description)
        => new()
        {
            Id = id, FfmpegName = ffmpegName, Vendor = vendor,
            Codec = codec, DisplayName = displayName, Description = description,
        };

    public static HardwareEncoderDefinition? Find(string encoderId)
        => Known.FirstOrDefault(e => e.Id == encoderId);

    /// <summary>Results of the last detection run, or an empty list if it hasn't happened.</summary>
    public IReadOnlyList<HardwareEncoderStatus> Statuses => _cached ?? [];

    public bool IsUsable(string encoderId)
        => _cached?.FirstOrDefault(s => s.Encoder.Id == encoderId)?.Usable ?? false;

    /// <summary>
    /// Probe every known encoder. Results are cached; pass <paramref name="force"/> to
    /// re-run after a driver or hardware change.
    /// </summary>
    public async Task<IReadOnlyList<HardwareEncoderStatus>> DetectAsync(
        string ffmpegPath, bool force = false, CancellationToken ct = default)
    {
        if (_cached is not null && !force) return _cached;

        var compiled = await ListCompiledEncodersAsync(ffmpegPath, ct);
        _logger.LogInformation("FFmpeg build advertises {Count} hardware encoder(s)", compiled.Count);

        // Probing is IO-bound on the GPU driver; a handful at a time is plenty and
        // avoids hammering a device that may only allow a couple of sessions.
        using var gate = new SemaphoreSlim(3);

        var probes = Known.Select(async definition =>
        {
            if (!compiled.Contains(definition.FfmpegName))
            {
                return new HardwareEncoderStatus
                {
                    Encoder = definition,
                    CompiledIn = false,
                    Usable = false,
                    Reason = "not included in this FFmpeg build",
                };
            }

            await gate.WaitAsync(ct);
            try
            {
                var (ok, reason) = await SmokeTestAsync(ffmpegPath, definition, ct);
                return new HardwareEncoderStatus
                {
                    Encoder = definition,
                    CompiledIn = true,
                    Usable = ok,
                    Reason = ok ? null : reason,
                };
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(probes);
        _cached = results;

        var usable = results.Where(r => r.Usable).Select(r => r.Encoder.Id).ToList();
        _logger.LogInformation("Usable hardware encoders: {Encoders}",
            usable.Count > 0 ? string.Join(", ", usable) : "(none)");

        return _cached;
    }

    private async Task<HashSet<string>> ListCompiledEncodersAsync(string ffmpegPath, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return found;

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            foreach (var definition in Known)
            {
                if (stdout.Contains(definition.FfmpegName, StringComparison.Ordinal))
                    found.Add(definition.FfmpegName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list FFmpeg encoders");
        }

        return found;
    }

    /// <summary>
    /// Pull the useful line out of FFmpeg's stderr. The last line is usually a generic
    /// consequence ("Nothing was written into output file"); the cause is further up.
    /// </summary>
    internal static string SummarizeFailure(string stderr, int exitCode)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => l.Length > 0)
                          .ToList();

        if (lines.Count == 0) return $"exit code {exitCode}";

        string[] causes =
        [
            "No device available", "Cannot load", "Failed to", "failed to", "not supported",
            "No capable devices", "Error creating", "Cannot open", "Device creation failed",
            "does not support", "No such device", "Error initializing", "Function not implemented",
        ];

        var cause = lines.FirstOrDefault(l => causes.Any(c => l.Contains(c, StringComparison.OrdinalIgnoreCase)));
        if (cause is not null) return Shorten(cause);

        // Otherwise take the first line that isn't just the generic tail.
        var meaningful = lines.FirstOrDefault(l => !l.Contains("Nothing was written", StringComparison.Ordinal));
        return Shorten(meaningful ?? lines[^1]);

        static string Shorten(string line)
        {
            // Drop FFmpeg's "[component @ 0x...]" prefix, which is noise to a user.
            var close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0 && line.StartsWith('[')) line = line[(close + 2)..];
            return line.Length <= 160 ? line : line[..160] + "…";
        }
    }

    /// <summary>Encode a couple of synthetic frames. The only honest availability test.</summary>
    private async Task<(bool Ok, string? Reason)> SmokeTestAsync(
        string ffmpegPath, HardwareEncoderDefinition definition, CancellationToken ct)
    {
        // 256x144 keeps this well inside every encoder's minimum dimensions.
        var args = definition.Vendor == HardwareVendor.Vaapi
            ? $"-hide_banner -v error -vaapi_device /dev/dri/renderD128 " +
              $"-f lavfi -i testsrc2=size=256x144:rate=25:duration=0.2 " +
              $"-vf format=nv12,hwupload -c:v {definition.FfmpegName} -f null -"
            : $"-hide_banner -v error " +
              $"-f lavfi -i testsrc2=size=256x144:rate=25:duration=0.2 " +
              $"-c:v {definition.FfmpegName} -f null -";

        try
        {
            var psi = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return (false, "could not start FFmpeg");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                return (false, "probe timed out");
            }

            if (process.ExitCode == 0) return (true, null);

            var stderr = await stderrTask;
            var reason = SummarizeFailure(stderr, process.ExitCode);

            _logger.LogDebug("{Encoder} unavailable: {Reason}", definition.FfmpegName, reason);
            return (false, reason);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
