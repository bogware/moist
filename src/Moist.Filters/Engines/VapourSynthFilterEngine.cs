using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Moist.Filters.Engines;

/// <summary>
/// VapourSynth filter engine — cross-platform scripting for advanced filtering.
/// Translates FilterChain into .vpy scripts.
/// </summary>
public sealed class VapourSynthFilterEngine : IFilterEngine
{
    private readonly string _vspipePath;

    public string Id => "vapoursynth";
    public string Name => "VapourSynth";
    public bool IsAvailable { get; private set; }

    public VapourSynthFilterEngine(string vspipePath = "vspipe")
    {
        _vspipePath = vspipePath;
    }

    public IReadOnlyList<FilterDefinition> GetAvailableFilters() =>
    [
        new FilterDefinition
        {
            Id = "vs_crop", Name = "Crop", Category = FilterCategory.Crop,
            Parameters = new()
            {
                ["left"] = new() { Name = "Left", Type = FilterParameterType.Int, DefaultValue = 0 },
                ["top"] = new() { Name = "Top", Type = FilterParameterType.Int, DefaultValue = 0 },
                ["right"] = new() { Name = "Right", Type = FilterParameterType.Int, DefaultValue = 0 },
                ["bottom"] = new() { Name = "Bottom", Type = FilterParameterType.Int, DefaultValue = 0 },
            }
        },
        new FilterDefinition
        {
            Id = "vs_resize", Name = "Resize", Category = FilterCategory.Resize,
            Parameters = new()
            {
                ["width"] = new() { Name = "Width", Type = FilterParameterType.Int, DefaultValue = 1920 },
                ["height"] = new() { Name = "Height", Type = FilterParameterType.Int, DefaultValue = 1080 },
                ["kernel"] = new()
                {
                    Name = "Kernel", Type = FilterParameterType.Choice, DefaultValue = "Lanczos",
                    Options = ["Bilinear", "Bicubic", "Lanczos", "Spline16", "Spline36", "Point"]
                },
            }
        },
        new FilterDefinition
        {
            Id = "vs_yadifmod", Name = "Yadifmod Deinterlace", Category = FilterCategory.Deinterlace,
            Description = "VapourSynth yadifmod deinterlacer",
            Parameters = new()
            {
                ["order"] = new()
                {
                    Name = "Field Order", Type = FilterParameterType.Choice, DefaultValue = "tff",
                    Options = ["tff", "bff"]
                },
            }
        },
        new FilterDefinition
        {
            Id = "vs_bm3d", Name = "BM3D Denoise", Category = FilterCategory.Denoise,
            Description = "Block-Matching and 3D filtering (high quality denoiser)",
            Parameters = new()
            {
                ["sigma"] = new() { Name = "Sigma", Type = FilterParameterType.Double, DefaultValue = 3.0, MinValue = 0.0, MaxValue = 50.0 },
            }
        },
        new FilterDefinition
        {
            Id = "vs_superres", Name = "AI Super-Resolution (vs-mlrt)", Category = FilterCategory.Resize,
            Description = "Neural super-resolution via vs-mlrt (ONNX / TensorRT / OpenVINO) — the "
                          + "temporally-aware quality ceiling. Needs VapourSynth + vs-mlrt + an ONNX model.",
            Parameters = new()
            {
                ["model"] = new() { Name = "ONNX model path", Type = FilterParameterType.String, DefaultValue = "" },
                ["backend"] = new()
                {
                    Name = "Backend", Type = FilterParameterType.Choice, DefaultValue = "ort",
                    Options = ["ort", "trt", "ov"] // onnxruntime / TensorRT / OpenVINO
                },
            }
        },
    ];

    public string GenerateScript(FilterChain chain, string inputFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import vapoursynth as vs");
        sb.AppendLine("core = vs.core");
        sb.AppendLine();
        // Escape backslashes and quotes for Python string safety
        var safeInputFile = inputFile.Replace("\\", "\\\\").Replace("\"", "\\\"");
        sb.AppendLine($"clip = core.lsmas.LWLibavSource(\"{safeInputFile}\")");

        foreach (var applied in chain.Filters)
        {
            switch (applied.Filter.Id)
            {
                case "vs_crop":
                    var l = GetInt(applied, "left");
                    var t = GetInt(applied, "top");
                    var r = GetInt(applied, "right");
                    var b = GetInt(applied, "bottom");
                    sb.AppendLine($"clip = core.std.Crop(clip, left={l}, top={t}, right={r}, bottom={b})");
                    break;

                case "vs_resize":
                    var w = GetInt(applied, "width", 1920);
                    var h = GetInt(applied, "height", 1080);
                    var kernel = GetString(applied, "kernel", "Lanczos");
                    sb.AppendLine($"clip = core.resize.{kernel}(clip, width={w}, height={h})");
                    break;

                case "vs_yadifmod":
                    var order = GetString(applied, "order", "tff") == "tff" ? 1 : 0;
                    sb.AppendLine($"clip = core.yadifmod.Yadifmod(clip, order={order})");
                    break;

                case "vs_bm3d":
                    var sigma = GetDouble(applied, "sigma", 3.0);
                    sb.AppendLine($"ref = core.bm3d.BM3D(clip, sigma={Fmt(sigma)})");
                    sb.AppendLine($"clip = core.bm3d.VAggregate(ref)");
                    break;

                case "vs_superres":
                    var model = GetString(applied, "model", "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                    var ns = GetString(applied, "backend", "ort") switch { "trt" => "trt", "ov" => "ov", _ => "ort" };
                    // vs-mlrt models want planar RGB float; convert in, run the network, convert back.
                    sb.AppendLine("clip = core.resize.Bicubic(clip, format=vs.RGBS, matrix_in_s=\"709\")");
                    sb.AppendLine($"clip = core.{ns}.Model(clip, network_path=\"{model}\")");
                    sb.AppendLine("clip = core.resize.Bicubic(clip, format=vs.YUV420P8, matrix_s=\"709\")");
                    break;
            }
        }

        sb.AppendLine();
        sb.AppendLine("clip.set_output()");

        return sb.ToString();
    }

    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _vspipePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return IsAvailable = false;
            await process.WaitForExitAsync(ct);
            return IsAvailable = process.ExitCode == 0;
        }
        catch
        {
            return IsAvailable = false;
        }
    }

    private static int GetInt(AppliedFilter af, string key, int def = 0)
        => af.ParameterValues.TryGetValue(key, out var v) ? Convert.ToInt32(v) : def;

    private static double GetDouble(AppliedFilter af, string key, double def = 0)
        => af.ParameterValues.TryGetValue(key, out var v) ? Convert.ToDouble(v) : def;

    private static string GetString(AppliedFilter af, string key, string def = "")
        => af.ParameterValues.TryGetValue(key, out var v) ? v.ToString() ?? def : def;

    private static string Fmt(double v) => v.ToString("G", CultureInfo.InvariantCulture);
}
