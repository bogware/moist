using Moist.Core.Media;
using Moist.Filters;
using Moist.Filters.Engines;

namespace Moist.Core.Tests;

public class FilterEngineTests
{
    // === FFmpeg Filter Engine ===

    [Fact]
    public void FFmpegEngine_GetAvailableFilters_ReturnsFilters()
    {
        var engine = new FFmpegFilterEngine();
        var filters = engine.GetAvailableFilters();
        Assert.NotEmpty(filters);
        Assert.Contains(filters, f => f.Id == "crop");
        Assert.Contains(filters, f => f.Id == "resize");
        Assert.Contains(filters, f => f.Id == "eq");
        Assert.Contains(filters, f => f.Id == "nlmeans");
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_EmptyChain_ReturnsEmpty()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Equal(string.Empty, script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_CropFilter()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var cropFilter = engine.GetAvailableFilters().First(f => f.Id == "crop");
        chain.Add(cropFilter, new Dictionary<string, object>
        {
            ["left"] = 10, ["top"] = 20, ["right"] = 10, ["bottom"] = 20
        });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Equal("crop=iw-20:ih-40:10:20", script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_ResizeFilter()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var resizeFilter = engine.GetAvailableFilters().First(f => f.Id == "resize");
        chain.Add(resizeFilter, new Dictionary<string, object>
        {
            ["width"] = 1920, ["height"] = 1080, ["algorithm"] = "lanczos"
        });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Equal("scale=1920:1080:flags=lanczos", script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_MultipleFilters_JoinsWithComma()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var filters = engine.GetAvailableFilters();

        chain.Add(filters.First(f => f.Id == "crop"), new Dictionary<string, object>
        {
            ["left"] = 0, ["top"] = 0, ["right"] = 0, ["bottom"] = 0
        });
        chain.Add(filters.First(f => f.Id == "fps"), new Dictionary<string, object>
        {
            ["fps"] = 24.0
        });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Contains(",", script);
        Assert.Contains("crop=", script);
        Assert.Contains("fps=24", script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_DebandFilter_DefaultBlurIsTrue()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var debandFilter = engine.GetAvailableFilters().First(f => f.Id == "deband");
        // Only set range, not blur — blur should default to true
        chain.Add(debandFilter, new Dictionary<string, object> { ["range"] = 16 });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Contains("blur=1", script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_CasFilter()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var cas = engine.GetAvailableFilters().First(f => f.Id == "cas");
        chain.Add(cas, new Dictionary<string, object> { ["strength"] = 0.5 });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Equal("cas=strength=0.5", script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_DeblockFilter_EmitsSpp()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        var deblock = engine.GetAvailableFilters().First(f => f.Id == "deblock");
        chain.Add(deblock, new Dictionary<string, object> { ["quality"] = 6, ["strength"] = 4 });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Equal("spp=quality=6:qp=4", script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_HandlesJsonElementParams_FromSerializedProfile()
    {
        // A profile's filter params survive a round-trip to JSON as JsonElement, not primitives.
        var engine = new FFmpegFilterEngine();
        var resize = engine.GetAvailableFilters().First(f => f.Id == "resize");
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["width"] = -2, ["height"] = 1080, ["algorithm"] = "spline"
        });
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

        var chain = new FilterChain();
        chain.Add(resize, roundTripped);

        Assert.Equal("scale=-2:1080:flags=spline", engine.GenerateScript(chain, "in.mkv"));
    }

    [Fact]
    public void FFmpegEngine_SuperRes_EmitsLibplaceboChain_WhenAvailable()
    {
        var engine = new FFmpegFilterEngine { IsSuperResAvailable = () => true };
        var chain = new FilterChain();
        chain.Add(engine.GetAvailableFilters().First(f => f.Id == "superres"),
            new Dictionary<string, object> { ["model"] = "fsrcnnx", ["height"] = 1080 });

        var script = engine.GenerateScript(chain, "in.mkv");
        Assert.Contains("hwupload", script);
        Assert.Contains("libplacebo=w=-2:h=1080", script);
        Assert.Contains("custom_shader_path=FSRCNNX_x2_8-0-4-1.glsl", script);
        Assert.Contains("hwdownload", script);
    }

    [Fact]
    public void FFmpegEngine_SuperRes_FallsBackToSpline_WhenGpuUnavailable()
    {
        var engine = new FFmpegFilterEngine { IsSuperResAvailable = () => false };
        var chain = new FilterChain();
        chain.Add(engine.GetAvailableFilters().First(f => f.Id == "superres"),
            new Dictionary<string, object> { ["model"] = "fsrcnnx", ["height"] = 720 });

        var script = engine.GenerateScript(chain, "in.mkv");
        Assert.Equal("scale=-2:720:flags=spline", script);
        Assert.DoesNotContain("libplacebo", script);
    }

    [Fact]
    public void GpuShaders_MapsKnownModelsToGlslFiles()
    {
        Assert.EndsWith(".glsl", GpuShaders.FileNameFor("fsrcnnx"));
        Assert.EndsWith(".glsl", GpuShaders.FileNameFor("anime4k"));
        Assert.Null(GpuShaders.FileNameFor("not-a-model"));
    }

    [Theory]
    [InlineData("mci", "mi_mode=mci")]
    [InlineData("blend", "mi_mode=blend")]
    public void FFmpegEngine_Minterpolate_EmitsMotionCompensatedFps(string mode, string expectedMode)
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        chain.Add(engine.GetAvailableFilters().First(f => f.Id == "minterpolate"),
            new Dictionary<string, object> { ["fps"] = 60.0, ["mode"] = mode });

        var script = engine.GenerateScript(chain, "in.mkv");
        Assert.Contains("minterpolate=fps=60", script);
        Assert.Contains(expectedMode, script);
    }

    [Fact]
    public void FFmpegEngine_GenerateScript_UnknownFilter_SkipsIt()
    {
        var engine = new FFmpegFilterEngine();
        var chain = new FilterChain();
        chain.Add(new FilterDefinition
        {
            Id = "nonexistent_filter", Name = "Bad", Category = FilterCategory.Other
        });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Equal(string.Empty, script);
        Assert.Contains("nonexistent_filter", engine.SkippedFilters);
    }

    [Fact]
    public void FFmpegEngine_IsAvailable_ReturnsTrue()
    {
        var engine = new FFmpegFilterEngine();
        Assert.True(engine.IsAvailable);
    }

    // === VapourSynth Filter Engine ===

    [Fact]
    public void VapourSynthEngine_GetAvailableFilters_ReturnsFilters()
    {
        var engine = new VapourSynthFilterEngine();
        var filters = engine.GetAvailableFilters();
        Assert.NotEmpty(filters);
        Assert.Contains(filters, f => f.Id == "vs_crop");
        Assert.Contains(filters, f => f.Id == "vs_resize");
    }

    [Fact]
    public void VapourSynthEngine_GenerateScript_HasImportAndOutput()
    {
        var engine = new VapourSynthFilterEngine();
        var chain = new FilterChain();
        var cropFilter = engine.GetAvailableFilters().First(f => f.Id == "vs_crop");
        chain.Add(cropFilter, new Dictionary<string, object>
        {
            ["left"] = 10, ["top"] = 20, ["right"] = 10, ["bottom"] = 20
        });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Contains("import vapoursynth as vs", script);
        Assert.Contains("clip.set_output()", script);
        Assert.Contains("core.std.Crop", script);
    }

    // === AviSynth Filter Engine ===

    [Fact]
    public void AviSynthEngine_GetAvailableFilters_ReturnsFilters()
    {
        var engine = new AviSynthFilterEngine();
        var filters = engine.GetAvailableFilters();
        Assert.NotEmpty(filters);
        Assert.Contains(filters, f => f.Id == "avs_crop");
        Assert.Contains(filters, f => f.Id == "avs_resize");
    }

    [Fact]
    public void AviSynthEngine_GenerateScript_ContainsSource()
    {
        var engine = new AviSynthFilterEngine();
        var chain = new FilterChain();
        var cropFilter = engine.GetAvailableFilters().First(f => f.Id == "avs_crop");
        chain.Add(cropFilter, new Dictionary<string, object>
        {
            ["left"] = 0, ["top"] = 0, ["right"] = 0, ["bottom"] = 0
        });

        var script = engine.GenerateScript(chain, "input.mkv");
        Assert.Contains("FFmpegSource2", script);
        Assert.Contains("Crop(", script);
    }

    // === Filter Chain ===

    [Fact]
    public void FilterChain_Add_AddsFilter()
    {
        var chain = new FilterChain();
        var filter = new FilterDefinition { Id = "test", Name = "Test", Category = FilterCategory.Other };
        chain.Add(filter);
        Assert.Single(chain.Filters);
    }

    [Fact]
    public void FilterChain_Remove_RemovesFilter()
    {
        var chain = new FilterChain();
        var filter = new FilterDefinition { Id = "test", Name = "Test", Category = FilterCategory.Other };
        chain.Add(filter);
        chain.Remove(0);
        Assert.Empty(chain.Filters);
    }

    [Fact]
    public void FilterChain_MoveUp_SwapsFilters()
    {
        var chain = new FilterChain();
        var f1 = new FilterDefinition { Id = "a", Name = "A", Category = FilterCategory.Other };
        var f2 = new FilterDefinition { Id = "b", Name = "B", Category = FilterCategory.Other };
        chain.Add(f1);
        chain.Add(f2);
        chain.MoveUp(1);
        Assert.Equal("b", chain.Filters[0].Filter.Id);
        Assert.Equal("a", chain.Filters[1].Filter.Id);
    }

    [Fact]
    public void FilterChain_MoveDown_SwapsFilters()
    {
        var chain = new FilterChain();
        var f1 = new FilterDefinition { Id = "a", Name = "A", Category = FilterCategory.Other };
        var f2 = new FilterDefinition { Id = "b", Name = "B", Category = FilterCategory.Other };
        chain.Add(f1);
        chain.Add(f2);
        chain.MoveDown(0);
        Assert.Equal("b", chain.Filters[0].Filter.Id);
        Assert.Equal("a", chain.Filters[1].Filter.Id);
    }

    // === Parameter Validation ===

    [Fact]
    public void AppliedFilter_Validate_ValidParams_NoErrors()
    {
        var filter = new FilterDefinition
        {
            Id = "test", Name = "Test", Category = FilterCategory.Other,
            Parameters = new()
            {
                ["value"] = new() { Name = "Value", Type = FilterParameterType.Int, MinValue = 0, MaxValue = 100 }
            }
        };
        var applied = new AppliedFilter
        {
            Filter = filter,
            ParameterValues = new() { ["value"] = 50 }
        };
        Assert.Empty(applied.Validate());
    }

    [Fact]
    public void AppliedFilter_Validate_OutOfRange_ReturnsError()
    {
        var filter = new FilterDefinition
        {
            Id = "test", Name = "Test", Category = FilterCategory.Other,
            Parameters = new()
            {
                ["value"] = new() { Name = "Value", Type = FilterParameterType.Int, MinValue = 0, MaxValue = 100 }
            }
        };
        var applied = new AppliedFilter
        {
            Filter = filter,
            ParameterValues = new() { ["value"] = 200 }
        };
        var errors = applied.Validate();
        Assert.NotEmpty(errors);
        Assert.Contains("above maximum", errors[0]);
    }

    [Fact]
    public void AppliedFilter_Validate_InvalidChoice_ReturnsError()
    {
        var filter = new FilterDefinition
        {
            Id = "test", Name = "Test", Category = FilterCategory.Other,
            Parameters = new()
            {
                ["algo"] = new()
                {
                    Name = "Algorithm", Type = FilterParameterType.Choice,
                    Options = ["bilinear", "lanczos"]
                }
            }
        };
        var applied = new AppliedFilter
        {
            Filter = filter,
            ParameterValues = new() { ["algo"] = "invalid_option" }
        };
        var errors = applied.Validate();
        Assert.NotEmpty(errors);
        Assert.Contains("not a valid option", errors[0]);
    }

    // === Filter Registry ===

    [Fact]
    public void FilterRegistry_Register_AddsEngine()
    {
        var registry = new FilterRegistry();
        var engine = new FFmpegFilterEngine();
        registry.Register(engine);
        Assert.Contains(engine, registry.Engines);
    }

    [Fact]
    public void FilterRegistry_GetEngine_FindsById()
    {
        var registry = new FilterRegistry();
        registry.Register(new FFmpegFilterEngine());
        var found = registry.GetEngine("ffmpeg");
        Assert.NotNull(found);
        Assert.Equal("ffmpeg", found.Id);
    }
}
