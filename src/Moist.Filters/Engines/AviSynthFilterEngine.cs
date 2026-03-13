using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Moist.Filters.Engines;

/// <summary>
/// AviSynth filter engine — Windows-only, for legacy compatibility.
/// Translates FilterChain into .avs scripts.
/// Inspired by MeGUI's ScriptServer but with a cleaner architecture.
/// </summary>
public sealed class AviSynthFilterEngine : IFilterEngine
{
    public string Id => "avisynth";
    public string Name => "AviSynth+";
    public bool IsAvailable { get; private set; }

    public IReadOnlyList<FilterDefinition> GetAvailableFilters() =>
    [
        new FilterDefinition
        {
            Id = "avs_crop", Name = "Crop", Category = FilterCategory.Crop,
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
            Id = "avs_resize", Name = "Resize", Category = FilterCategory.Resize,
            Parameters = new()
            {
                ["width"] = new() { Name = "Width", Type = FilterParameterType.Int, DefaultValue = 1920 },
                ["height"] = new() { Name = "Height", Type = FilterParameterType.Int, DefaultValue = 1080 },
                ["method"] = new()
                {
                    Name = "Method", Type = FilterParameterType.Choice, DefaultValue = "LanczosResize",
                    Options = ["BilinearResize", "BicubicResize", "LanczosResize", "Lanczos4Resize", "BlackmanResize", "Spline16Resize", "Spline36Resize", "Spline64Resize"]
                },
            }
        },
        new FilterDefinition
        {
            Id = "avs_yadif", Name = "Yadif", Category = FilterCategory.Deinterlace,
            Description = "Yadif deinterlacer for AviSynth",
            Parameters = new()
            {
                ["mode"] = new()
                {
                    Name = "Mode", Type = FilterParameterType.Choice, DefaultValue = "0",
                    Options = ["0", "1", "2", "3"],
                    Description = "0=frame, 1=field, 2=frame no spatial, 3=field no spatial"
                },
            }
        },
        new FilterDefinition
        {
            Id = "avs_tdeint", Name = "TDeint", Category = FilterCategory.Deinterlace,
            Description = "TDeint with optional EEDI2 for edge interpolation",
            Parameters = new()
            {
                ["mode"] = new()
                {
                    Name = "Mode", Type = FilterParameterType.Choice, DefaultValue = "1",
                    Options = ["0", "1", "2"],
                    Description = "0=kernel, 1=ELA interpolation, 2=motion adaptive"
                },
                ["eedi2"] = new() { Name = "Use EEDI2", Type = FilterParameterType.Bool, DefaultValue = false },
            }
        },
        new FilterDefinition
        {
            Id = "avs_convolution3d", Name = "Convolution3D", Category = FilterCategory.Denoise,
            Parameters = new()
            {
                ["preset"] = new()
                {
                    Name = "Preset", Type = FilterParameterType.Choice, DefaultValue = "movieHQ",
                    Options = ["movieLQ", "movieHQ", "animeHQ"]
                },
            }
        },
        new FilterDefinition
        {
            Id = "avs_undot", Name = "Undot", Category = FilterCategory.Denoise,
            Description = "Simple dot noise remover",
            Parameters = new()
        },
    ];

    public string GenerateScript(FilterChain chain, string inputFile)
    {
        var sb = new StringBuilder();

        // Escape quotes in file path to prevent script injection
        var safeInputFile = inputFile.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Source - detect input type
        var ext = Path.GetExtension(inputFile).ToLowerInvariant();
        switch (ext)
        {
            case ".avs":
                sb.AppendLine($"Import(\"{safeInputFile}\")");
                break;
            case ".d2v":
                sb.AppendLine("LoadPlugin(\"DGDecode.dll\")");
                sb.AppendLine($"DGDecode_mpeg2source(\"{safeInputFile}\")");
                break;
            default:
                sb.AppendLine($"FFmpegSource2(\"{safeInputFile}\")");
                break;
        }

        foreach (var applied in chain.Filters)
        {
            switch (applied.Filter.Id)
            {
                case "avs_crop":
                    var l = GetInt(applied, "left");
                    var t = GetInt(applied, "top");
                    var r = GetInt(applied, "right");
                    var b = GetInt(applied, "bottom");
                    sb.AppendLine($"Crop({l}, {t}, -{r}, -{b})");
                    break;

                case "avs_resize":
                    var w = GetInt(applied, "width", 1920);
                    var h = GetInt(applied, "height", 1080);
                    var method = GetString(applied, "method", "LanczosResize");
                    sb.AppendLine($"{method}({w}, {h})");
                    break;

                case "avs_yadif":
                    var mode = GetString(applied, "mode", "0");
                    sb.AppendLine("LoadPlugin(\"yadifmod2.dll\")");
                    sb.AppendLine($"Yadif(mode={mode})");
                    break;

                case "avs_tdeint":
                    var tdMode = GetString(applied, "mode", "1");
                    var useEedi2 = GetBool(applied, "eedi2");
                    sb.AppendLine("LoadPlugin(\"TDeint.dll\")");
                    if (useEedi2)
                    {
                        sb.AppendLine("LoadPlugin(\"EEDI2.dll\")");
                        sb.AppendLine($"TDeint(mode={tdMode}, edeint=EEDI2(field=-1))");
                    }
                    else
                    {
                        sb.AppendLine($"TDeint(mode={tdMode})");
                    }
                    break;

                case "avs_convolution3d":
                    var preset = GetString(applied, "preset", "movieHQ");
                    sb.AppendLine("LoadPlugin(\"Convolution3DYV12.dll\")");
                    sb.AppendLine($"Convolution3D(preset=\"{preset}\")");
                    break;

                case "avs_undot":
                    sb.AppendLine("Undot()");
                    break;
            }
        }

        return sb.ToString();
    }

    public async Task<bool> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        // AviSynth is Windows-only
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return IsAvailable = false;

        // First check if AviSynth+ directory exists
        var dirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AviSynth+"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AviSynth+"),
        };

        if (!dirs.Any(Directory.Exists))
            return IsAvailable = false;

        // Try running avs2pipemod to verify AviSynth actually works
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "avs2pipemod",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return IsAvailable = true; // Directory exists, assume available

            await process.WaitForExitAsync(ct);
            return IsAvailable = true; // If avs2pipemod ran at all, AviSynth is installed
        }
        catch
        {
            // avs2pipemod not found, but directory exists — AviSynth is likely installed
            return IsAvailable = true;
        }
    }

    private static int GetInt(AppliedFilter af, string key, int def = 0)
        => af.ParameterValues.TryGetValue(key, out var v) ? Convert.ToInt32(v) : def;

    private static string GetString(AppliedFilter af, string key, string def = "")
        => af.ParameterValues.TryGetValue(key, out var v) ? v.ToString() ?? def : def;

    private static bool GetBool(AppliedFilter af, string key, bool def = false)
        => af.ParameterValues.TryGetValue(key, out var v) ? Convert.ToBoolean(v) : def;
}
