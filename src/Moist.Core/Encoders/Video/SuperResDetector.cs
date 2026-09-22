using System.Diagnostics;

namespace Moist.Core.Encoders.Video;

/// <summary>
/// Probes whether this machine can run the GPU "superres" upscaler — whether the configured FFmpeg
/// has libplacebo and a usable Vulkan device. Like <see cref="HardwareCapabilities"/>, a listed
/// capability isn't a working one: a build can advertise libplacebo on a box with no Vulkan driver,
/// so we actually run a tiny upscale and keep the answer only if it succeeds. When it fails, the
/// superres filter degrades to a plain spline scale, so the AI profile still produces HD output.
///
/// Results are cached per FFmpeg path for the process lifetime — the probe costs a second or two.
/// </summary>
public static class SuperResDetector
{
    private static readonly Dictionary<string, bool> _cache = new();
    private static readonly object _gate = new();

    public static bool IsAvailable(string ffmpegPath)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(ffmpegPath, out var cached)) return cached;
            var ok = Probe(ffmpegPath);
            _cache[ffmpegPath] = ok;
            return ok;
        }
    }

    /// <summary>Forget cached probe results (e.g. after the FFmpeg path changes in Settings).</summary>
    public static void ResetCache()
    {
        lock (_gate) _cache.Clear();
    }

    private static bool Probe(string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // ArgumentList (each token separate) avoids any quoting/escaping; the lavfi color source
            // needs no input file, so this touches nothing on disk.
            string[] args =
            [
                "-hide_banner", "-loglevel", "error",
                "-init_hw_device", "vulkan=vk:0", "-filter_hw_device", "vk",
                "-f", "lavfi", "-i", "color=c=black:s=64x64:d=1",
                "-vf", "format=yuv420p,hwupload,libplacebo=w=128:h=128,hwdownload,format=yuv420p",
                "-frames:v", "1", "-f", "null", "-",
            ];
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;
            // Drain both pipes while waiting: they're redirected, so leaving them unread risks a
            // deadlock if the probe writes more than the OS pipe buffer (Vulkan init can be chatty).
            var drainOut = p.StandardOutput.ReadToEndAsync();
            var drainErr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            try { Task.WaitAll([drainOut, drainErr], 2_000); } catch { /* reads race process exit */ }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
