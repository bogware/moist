namespace Moist.Core.Media;

/// <summary>
/// Locates the bundled mpv/libplacebo GLSL upscaling shaders that back the "superres" filter.
/// They ship in a <c>Shaders</c> folder next to the binary (copied to output by the .csproj).
///
/// The FFmpeg filter engine emits the <b>bare filename</b> in libplacebo's <c>custom_shader_path</c>,
/// and the video processor runs FFmpeg with its working directory set to <see cref="Directory"/>.
/// A bare filename dodges the filter-graph path escaping a drive-lettered Windows path would
/// otherwise need — the same trick <c>QualityMeasurer</c> uses for libvmaf's log_path.
/// </summary>
public static class GpuShaders
{
    /// <summary>Folder the shaders are deployed to, alongside the running binary.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Shaders");

    /// <summary>
    /// The shader filename for a superres model id, or null for an unknown model. Keep these in
    /// sync with the files bundled under <c>Moist.Core/Shaders</c>.
    /// </summary>
    public static string? FileNameFor(string model) => model.Trim().ToLowerInvariant() switch
    {
        "fsrcnnx" => "FSRCNNX_x2_8-0-4-1.glsl",       // general / live-action
        "anime4k" => "Anime4K_Upscale_CNN_x2_M.glsl", // anime / line-art
        _ => null
    };

    /// <summary>Full path to a model's shader on disk, or null if the model is unknown.</summary>
    public static string? PathFor(string model)
        => FileNameFor(model) is { } file ? Path.Combine(Directory, file) : null;

    /// <summary>
    /// True if a generated filter chain uses libplacebo (the superres GPU path). When it is, FFmpeg
    /// needs a Vulkan device declared before the input and its working directory set to
    /// <see cref="Directory"/> so the bare shader filename resolves.
    /// </summary>
    public static bool UsedInFilterScript(string? filterScript)
        => filterScript?.Contains("libplacebo", StringComparison.Ordinal) == true;
}
