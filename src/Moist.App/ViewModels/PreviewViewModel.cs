using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Core.Media;
using Moist.Services;

namespace Moist.App.ViewModels;

/// <summary>
/// Drives the resizable preview window. It renders a short snippet through the REAL encode
/// pipeline (so the picture reflects every setting — filters, encoder, bit depth, AI upscale,
/// …) and shows it either as a scrubbable output frame or side-by-side against the untouched
/// source. Motion playback is delegated to the system player via "Open in player".
/// </summary>
public partial class PreviewViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly Func<EncodeRequest?> _requestFactory;
    private readonly MediaFileInfo? _info;
    private readonly string _inputFile;

    /// <summary>Where in the SOURCE the snippet begins (seconds).</summary>
    [ObservableProperty] private double _snippetStart;

    /// <summary>Upper bound for the start slider (source duration minus a little).</summary>
    [ObservableProperty] private double _sourceDuration = 1;

    /// <summary>Scrub position WITHIN the rendered snippet (seconds).</summary>
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _snippetDuration = 1;

    /// <summary>Show source and output side by side; otherwise just the (larger) output.</summary>
    [ObservableProperty] private bool _sideBySide = true;

    [ObservableProperty] private Bitmap? _sourceImage;
    [ObservableProperty] private Bitmap? _outputImage;

    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private double _renderProgress;
    [ObservableProperty] private string _status = "Set a start point and click Generate to render a preview snippet.";
    [ObservableProperty] private bool _hasSnippet;

    private string? _snippetPath;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _scrubDebounce;

    public PreviewViewModel(AppServices services, Func<EncodeRequest?> requestFactory,
        MediaFileInfo? info, string inputFile)
    {
        _services = services;
        _requestFactory = requestFactory;
        _info = info;
        _inputFile = inputFile;

        if (info is { Duration.TotalSeconds: > 0 })
        {
            SourceDuration = info.Duration.TotalSeconds;
            _snippetStart = info.Duration.TotalSeconds * 0.1; // a representative spot, not the titles
        }
    }

    [RelayCommand]
    private async Task GeneratePreview()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        IsRendering = true;
        RenderProgress = 0;
        Status = "Rendering snippet through the full pipeline… (uses the real encoder, so this takes about as long as encoding that many seconds)";
        var start = SnippetStart;
        var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => RenderProgress = p));
        try
        {
            // Build the request AND render off the UI thread — BuildPreviewRequest calls
            // GenerateScript, whose superres path runs a synchronous probe that would otherwise
            // freeze the window. Returns null when there's nothing to preview.
            var result = await Task.Run(async () =>
            {
                var request = _requestFactory();
                if (request is null || request.AudioOnly) return (SnippetPreviewResult?)null;
                return await _services.SnippetPreview.RenderAsync(
                    request, _info, TimeSpan.FromSeconds(start), duration: null, progress, ct);
            });

            if (result is null)
            {
                Status = "Nothing to preview — load a video on the Encode tab (and turn off Audio-only).";
                return;
            }

            _snippetPath = result.SnippetPath;
            SnippetDuration = Math.Max(0.1, result.DurationSeconds);
            HasSnippet = true;
            Position = 0;
            await RefreshFramesAsync(ct);
            Status = $"Preview ready — {result.DurationSeconds:F1}s snippet. Scrub to inspect, or open it in your player for motion.";
        }
        catch (OperationCanceledException)
        {
            Status = "Preview cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Preview failed: {ex.Message}";
            HasSnippet = false;
        }
        finally
        {
            IsRendering = false;
        }
    }

    [RelayCommand]
    private void OpenInPlayer()
    {
        if (_snippetPath is null || !File.Exists(_snippetPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _snippetPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open the player: {ex.Message}";
        }
    }

    partial void OnPositionChanged(double value) => _ = ScrubDebouncedAsync();
    partial void OnSideBySideChanged(bool value) => _ = RefreshFramesAsync(CancellationToken.None);

    private async Task ScrubDebouncedAsync()
    {
        _scrubDebounce?.Cancel();
        _scrubDebounce?.Dispose();
        _scrubDebounce = new CancellationTokenSource();
        var ct = _scrubDebounce.Token;
        try
        {
            await Task.Delay(120, ct);
            await RefreshFramesAsync(ct);
        }
        catch (OperationCanceledException) { /* superseded by a newer scrub */ }
    }

    /// <summary>
    /// Pull the output frame from the rendered snippet at the scrub position, and (for side-by-side)
    /// the untouched source frame at the corresponding source timestamp.
    /// </summary>
    private async Task RefreshFramesAsync(CancellationToken ct)
    {
        if (!HasSnippet || _snippetPath is null) return;
        try
        {
            var outPath = await _services.PreviewService.ExtractFrameAsync(
                _snippetPath, TimeSpan.FromSeconds(Position), maxWidth: 1280, ct: ct);
            if (outPath is not null) SetImage(b => OutputImage = b, ref _outputBacking, outPath);

            if (SideBySide)
            {
                var srcPath = await _services.PreviewService.ExtractFrameAsync(
                    _inputFile, TimeSpan.FromSeconds(SnippetStart + Position), maxWidth: 1280, ct: ct);
                if (srcPath is not null) SetImage(b => SourceImage = b, ref _sourceBacking, srcPath);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = $"Couldn't read a frame: {ex.Message}"; }
    }

    private Bitmap? _sourceBacking;
    private Bitmap? _outputBacking;

    private static void SetImage(Action<Bitmap?> assign, ref Bitmap? backing, string path)
    {
        Bitmap? loaded = null;
        try { loaded = new Bitmap(path); } catch { return; }
        var old = backing;
        backing = loaded;
        Dispatcher.UIThread.Post(() =>
        {
            assign(loaded);
            old?.Dispose();
        });
    }

    public void Dispose()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _scrubDebounce?.Cancel();
        _scrubDebounce?.Dispose();
        SourceImage?.Dispose();
        OutputImage?.Dispose();
    }
}
