using Moist.Services;

namespace Moist.IntegrationTests;

/// <summary>
/// Guards the snippet-preview pipeline: rendering a short span through the real processor must
/// produce a playable file. (The UI marshaling is exercised by hand; this covers the service.)
/// </summary>
[Collection("media")]
public class SnippetPreviewTests(MediaFixture media) : PipelineTestBase(media)
{
    [FfmpegFact]
    public async Task RendersSnippetThroughPipeline()
    {
        var services = CreateServices();
        var info = await services.MediaAnalyzer.AnalyzeAsync(Media.NaturalVideo);

        var request = new EncodeRequest
        {
            InputFile = Media.NaturalVideo,
            OutputFile = Media.Output("preview_dummy.mkv"),
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x265",
        };

        var result = await services.SnippetPreview.RenderAsync(
            request, info, TimeSpan.FromSeconds(1));

        Assert.True(File.Exists(result.SnippetPath), $"no snippet at {result.SnippetPath}");
        Assert.True(new FileInfo(result.SnippetPath).Length > 0, "snippet is empty");
    }

    [FfmpegFact]
    public async Task RendersWithoutMediaInfo()
    {
        // The GUI may preview before an explicit analyze; a null MediaFileInfo must still work.
        var services = CreateServices();
        var request = new EncodeRequest
        {
            InputFile = Media.NaturalVideo,
            OutputFile = Media.Output("preview_noinfo.mkv"),
            TempDirectory = Media.TempDir,
            VideoEncoderId = "ffmpeg-x264",
        };

        var result = await services.SnippetPreview.RenderAsync(request, info: null, TimeSpan.Zero);
        Assert.True(File.Exists(result.SnippetPath));
    }
}
