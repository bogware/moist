using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Services;
using Moist.Core.Chunking;
using Moist.Core.Encoders;
using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Moist.Core.Profiles;
using Moist.Core.Quality;

namespace Moist.App.ViewModels;

public partial class EncodeTabViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly FilterTabViewModel? _filterTab;

    // Input
    [ObservableProperty] private string _inputFile = string.Empty;
    [ObservableProperty] private string _outputFile = string.Empty;
    [ObservableProperty] private MediaFileInfo? _mediaInfo;

    // Video encoder
    [ObservableProperty] private EncoderInfo? _selectedVideoEncoder;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQualityMode))]
    [NotifyPropertyChangedFor(nameof(IsBitrateMode))]
    private VideoEncodingMode _encodingMode = VideoEncodingMode.ConstantRateFactor;
    [ObservableProperty] private double _quality = 23.0;
    [ObservableProperty] private int _bitrate = 5000;
    [ObservableProperty] private VideoPreset _preset = VideoPreset.Medium;
    [ObservableProperty] private VideoTune _tune = VideoTune.None;
    [ObservableProperty] private int _svtAv1Preset = 6;
    [ObservableProperty] private string _customVideoArgs = string.Empty;

    // SVT-AV1 advanced knobs (only meaningful for the svtav1 encoder; ignored otherwise).
    [ObservableProperty] private SvtAv1Tune? _svtAv1Tune;
    [ObservableProperty] private int _svtAv1FilmGrain;               // 0 = off
    [ObservableProperty] private int _svtAv1TileRows = 1;
    [ObservableProperty] private int _svtAv1TileColumns = 1;
    [ObservableProperty] private int _svtAv1KeyframeInterval = 240;

    // AI / frame-rate
    [ObservableProperty] private bool _aiUpscaleAllowLong;
    /// <summary>RIFE AI interpolation target fps (0 = off). Routes the job through RifeVideoProcessor.</summary>
    [ObservableProperty] private double _rifeTargetFps;

    /// <summary>True when the mode is quality-driven (CRF/CQ) — the Quality slider applies.</summary>
    public bool IsQualityMode => EncodingMode is VideoEncodingMode.ConstantRateFactor or VideoEncodingMode.ConstantQuality;
    /// <summary>True when the mode is bitrate-driven — the Bitrate box applies.</summary>
    public bool IsBitrateMode => EncodingMode is VideoEncodingMode.SinglePassBitrate or VideoEncodingMode.TwoPassBitrate;

    // Audio encoder
    [ObservableProperty] private EncoderInfo? _selectedAudioEncoder;
    [ObservableProperty] private int _audioBitrate = 128;
    [ObservableProperty] private AudioBitrateMode _audioBitrateMode = AudioBitrateMode.Vbr;
    [ObservableProperty] private AudioChannelMode _audioChannelMode = AudioChannelMode.KeepOriginal;
    [ObservableProperty] private int _audioSampleRate;  // 0 = keep original
    [ObservableProperty] private string _customAudioArgs = string.Empty;
    [ObservableProperty] private AudioNormalizationMode _audioNormalization = AudioNormalizationMode.None;
    [ObservableProperty] private double _normalizationTarget = -16.0;
    [ObservableProperty] private double _truePeakLimit = -1.0;
    [ObservableProperty] private AudioSampleFormat _audioSampleFormat = AudioSampleFormat.Auto;

    // Codec-specific settings
    [ObservableProperty] private int _opusComplexity = 10;
    [ObservableProperty] private string _aacProfile = "";
    [ObservableProperty] private int _mp3VbrScale = 2;
    [ObservableProperty] private int _flacCompressionLevel = 5;
    [ObservableProperty] private int _ac3DialogueNorm = -31;
    [ObservableProperty] private int _eac3DialogueNorm = -31;
    [ObservableProperty] private int _vorbisQuality = 3;

    // Audio-only mode
    [ObservableProperty] private bool _audioOnly;

    // Container
    [ObservableProperty] private ContainerFormat _container = ContainerFormat.Mkv;

    // Audio track selection
    [ObservableProperty] private int _selectedAudioTrackIndex;
    public ObservableCollection<string> AudioTrackLabels { get; } = [];

    /// <summary>Encode every audio track in the source rather than just the selected one.</summary>
    [ObservableProperty] private bool _encodeAllAudioTracks;

    // Container extras
    [ObservableProperty] private bool _copySubtitles = true;
    [ObservableProperty] private string _externalSubtitleFile = string.Empty;
    [ObservableProperty] private bool _copyChapters = true;
    [ObservableProperty] private bool _copyAttachments = true;
    [ObservableProperty] private bool _copyMetadata = true;

    /// <summary>Subtitle stream indices to keep (comma-separated, blank = all). Mirrors CLI --subtitle-track.</summary>
    [ObservableProperty] private string _subtitleTracks = string.Empty;

    /// <summary>Experimental, opt-in: carry Dolby Vision / HDR10+ dynamic metadata across the re-encode.</summary>
    [ObservableProperty] private bool _preserveDynamicHdr;
    public ObservableCollection<string> SubtitleTrackLabels { get; } = [];

    // Video bit depth and chroma
    [ObservableProperty] private VideoBitDepth _bitDepth = VideoBitDepth.Auto;
    public static VideoBitDepth[] BitDepthOptions { get; } = Enum.GetValues<VideoBitDepth>();

    /// <summary>Real-ESRGAN AI-upscale model, or null for none. Driven by the AI profile.</summary>
    [ObservableProperty] private string? _aiUpscaleModel;

    /// <summary>Target height for Real-ESRGAN upscaling. Carried from the profile so it round-trips
    /// with the model rather than silently defaulting to 1080 (no UI field yet — profile-driven).</summary>
    private int _aiUpscaleHeight = 1080;

    [ObservableProperty] private ChromaSubsampling _chroma = ChromaSubsampling.Auto;

    /// <summary>Standalone x265 only: how its untimed stream is wrapped (see X265StreamWrap).</summary>
    [ObservableProperty] private X265StreamWrap _x265StreamWrap = X265StreamWrap.NoBFrames;
    public static X265StreamWrap[] X265StreamWrapOptions { get; } = Enum.GetValues<X265StreamWrap>();

    /// <summary>The wrap choice only means something for the standalone x265 encoder.</summary>
    public bool IsX265Cli => SelectedVideoEncoder?.Id == "cli-x265";
    partial void OnSelectedVideoEncoderChanged(EncoderInfo? value) => OnPropertyChanged(nameof(IsX265Cli));
    public static ChromaSubsampling[] ChromaOptions { get; } = Enum.GetValues<ChromaSubsampling>();

    /// <summary>Render subtitles into the picture rather than muxing them as a track.</summary>
    [ObservableProperty] private bool _burnInSubtitles;
    [ObservableProperty] private int _burnInSubtitleTrack;

    /// <summary>Anything about this encode that will quietly lose something.</summary>
    [ObservableProperty] private string _encodeWarnings = string.Empty;

    // Chunked encoding
    [ObservableProperty] private bool _useChunkedEncoding;
    [ObservableProperty] private bool _chunkSceneDetection = true;
    [ObservableProperty] private int _maxChunkSeconds = 60;

    /// <summary>Give each scene its own CRF, aiming for the target quality everywhere.</summary>
    [ObservableProperty] private bool _usePerSceneQuality;

    // Target quality
    [ObservableProperty] private QualityMetric _targetMetric = QualityMetric.Vmaf;
    [ObservableProperty] private double _targetScore = 95;
    [ObservableProperty] private bool _isSearchingQuality;
    [ObservableProperty] private string _qualitySearchStatus = string.Empty;
    public static QualityMetric[] QualityMetricOptions { get; } = Enum.GetValues<QualityMetric>();

    private CancellationTokenSource? _searchCts;

    /// <summary>
    /// Find the CRF that reaches the target and apply it. Samples a few short spans
    /// rather than encoding the whole file, so this takes a minute or two rather than
    /// as long as the encode itself.
    /// </summary>
    [RelayCommand]
    private async Task FindTargetCrf()
    {
        if (IsSearchingQuality)
        {
            _searchCts?.Cancel();
            return;
        }

        if (MediaInfo is null || string.IsNullOrWhiteSpace(InputFile))
        {
            HasError = true;
            StatusMessage = "Analyze a file first — the search needs to know its duration.";
            return;
        }

        if (AudioOnly)
        {
            HasError = true;
            StatusMessage = "Target quality applies to video; it does nothing in audio-only mode.";
            return;
        }

        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        IsSearchingQuality = true;
        HasError = false;
        QualitySearchStatus = "Starting search…";

        try
        {
            var search = new TargetQualitySearch(_services, _services.LoggerFactory);
            search.Progress += (done, budget, message) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    QualitySearchStatus = $"[{done}/{budget}] {message}");

            var request = BuildRequest(InputFile, OutputFile);
            var options = new TargetQualityOptions { Metric = TargetMetric, Target = TargetScore };

            var result = await search.SearchAsync(request, options, MediaInfo, _searchCts.Token);

            EncodingMode = VideoEncodingMode.ConstantRateFactor;
            Quality = result.Crf;

            QualitySearchStatus = result.Summary
                + (result.EstimatedSizePercent is { } percent ? $"  ~{percent:F0}% of source" : "")
                + (result.FromCache ? "  (cached)" : "");

            if (!result.TargetMet)
            {
                HasError = true;
                StatusMessage = "Target not reached — using the best CRF found.";
            }
        }
        catch (OperationCanceledException)
        {
            QualitySearchStatus = "Search cancelled.";
        }
        catch (Exception ex)
        {
            HasError = true;
            QualitySearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingQuality = false;
        }
    }

    // Preview
    [ObservableProperty] private Bitmap? _previewImage;
    private MemoryStream? _previewStream;
    [ObservableProperty] private double _previewPosition;
    [ObservableProperty] private double _previewMaxPosition = 100;
    [ObservableProperty] private bool _isPreviewLoading;

    // Output preview: the SAME frame as the source scrub position, but rendered through the full
    // pipeline (filters + encoder + bit depth + AI/RIFE) so it's a true before/after. Rendered on
    // demand (it runs the real encoder, so it's not continuous like the source scrub).
    [ObservableProperty] private Bitmap? _outputPreviewImage;
    private MemoryStream? _outputPreviewStream;
    [ObservableProperty] private bool _isRenderingOutput;
    [ObservableProperty] private double _outputRenderProgress;
    [ObservableProperty] private string _outputPreviewStatus = string.Empty;
    private string? _outputSnippetPath;

    // Profiles
    [ObservableProperty] private EncodingProfile? _selectedProfile;
    public ObservableCollection<EncodingProfile> Profiles { get; } = [];

    // Available options
    public ObservableCollection<EncoderInfo> VideoEncoders { get; } = [];
    public ObservableCollection<EncoderInfo> AudioEncoders { get; } = [];

    // Static options for XAML binding
    public static VideoEncodingMode[] EncodingModeOptions { get; } = Enum.GetValues<VideoEncodingMode>();
    public static VideoPreset[] PresetOptions { get; } = Enum.GetValues<VideoPreset>();
    public static VideoTune[] TuneOptions { get; } = Enum.GetValues<VideoTune>();
    public static ContainerFormat[] ContainerOptions { get; } = Enum.GetValues<ContainerFormat>();
    public static AudioBitrateMode[] AudioBitrateModeOptions { get; } = Enum.GetValues<AudioBitrateMode>();
    public static AudioChannelMode[] AudioChannelModeOptions { get; } = Enum.GetValues<AudioChannelMode>();
    public static AudioNormalizationMode[] NormalizationModeOptions { get; } = Enum.GetValues<AudioNormalizationMode>();
    public static AudioSampleFormat[] SampleFormatOptions { get; } = Enum.GetValues<AudioSampleFormat>();
    public static int[] SampleRateOptions { get; } = [0, 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];
    public static string[] AacProfileOptions { get; } = ["", "aac_low", "aac_he", "aac_he_v2", "aac_ld", "aac_eld"];

    public string MediaInfoSummary => MediaInfo is null ? "" :
        $"Duration: {MediaInfo.Duration:hh\\:mm\\:ss}  |  " +
        (MediaInfo.Video is not null
            ? $"Video: {MediaInfo.Video.Width}x{MediaInfo.Video.Height} @ {MediaInfo.Video.Framerate:F3} fps ({MediaInfo.Video.Codec})  |  "
            : "") +
        (MediaInfo.AudioStreams.Count > 0
            ? $"Audio: {MediaInfo.AudioStreams[0].Codec} {MediaInfo.AudioStreams[0].Channels}ch ({MediaInfo.AudioStreams.Count} track(s))"
            : "No audio");

    // Drag-and-drop state
    [ObservableProperty] private bool _isDragOver;

    // Status / errors
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasError;

    public EncodeTabViewModel(AppServices services, FilterTabViewModel? filterTab = null)
    {
        _services = services;
        _filterTab = filterTab;

        // Hardware encoders are withheld until probing confirms they work — offering a
        // GPU encoder that isn't there just produces a baffling FFmpeg error later.
        foreach (var enc in services.Encoders.GetVideoEncoders().Where(e => !IsHardware(e.Id)))
            VideoEncoders.Add(enc);
        foreach (var enc in services.Encoders.GetAudioEncoders())
            AudioEncoders.Add(enc);

        SelectedVideoEncoder = VideoEncoders.FirstOrDefault();
        SelectedAudioEncoder = AudioEncoders.FirstOrDefault();

        RefreshProfiles();
        _ = AddUsableHardwareEncodersAsync();
    }

    private static bool IsHardware(string id) => id.StartsWith("hw-", StringComparison.Ordinal);

    /// <summary>
    /// Probe for GPU encoders in the background and add the ones that work. Runs off the
    /// constructor so startup isn't blocked on spawning FFmpeg a dozen times.
    /// </summary>
    private async Task AddUsableHardwareEncodersAsync()
    {
        try
        {
            var statuses = await _services.DetectHardwareAsync();
            var usable = statuses.Where(s => s.Usable).Select(s => s.Encoder.Id).ToHashSet(StringComparer.Ordinal);
            if (usable.Count == 0) return;

            var toAdd = _services.Encoders.GetVideoEncoders()
                .Where(e => usable.Contains(e.Id))
                .OrderBy(e => e.Name)
                .ToList();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var enc in toAdd)
                {
                    if (VideoEncoders.All(v => v.Id != enc.Id))
                        VideoEncoders.Add(enc);
                }
                HardwareStatus = $"{toAdd.Count} hardware encoder(s) available";
            });
        }
        catch
        {
            // Detection is a convenience; software encoding still works without it.
        }
    }

    [ObservableProperty] private string _hardwareStatus = string.Empty;

    // === Drag-and-Drop ===

    private CancellationTokenSource? _analysisCts;

    public async Task HandleDroppedFile(string filePath)
    {
        InputFile = filePath;
        OutputFile = string.Empty; // Reset so auto-generation kicks in
        await AnalyzeInput();
    }

    // === File Analysis ===

    [RelayCommand]
    private async Task AnalyzeInput()
    {
        if (string.IsNullOrWhiteSpace(InputFile)) return;

        if (!File.Exists(InputFile))
        {
            HasError = true;
            StatusMessage = $"File not found: {InputFile}";
            return;
        }

        // Cancel any in-flight analysis
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;

        HasError = false;
        StatusMessage = "Analyzing...";

        try
        {
            MediaInfo = await _services.MediaAnalyzer.AnalyzeAsync(InputFile, ct);
            ct.ThrowIfCancellationRequested();
            OnPropertyChanged(nameof(MediaInfoSummary));

            // Populate audio track selection
            AudioTrackLabels.Clear();
            for (int i = 0; i < MediaInfo.AudioStreams.Count; i++)
            {
                var a = MediaInfo.AudioStreams[i];
                var label = $"Track {i + 1}: {a.Codec} {a.Channels}ch";
                if (a.Language is not null) label += $" ({a.Language})";
                if (a.Title is not null) label += $" — {a.Title}";
                AudioTrackLabels.Add(label);
            }
            SelectedAudioTrackIndex = 0;

            // Populate subtitle track info
            SubtitleTrackLabels.Clear();
            for (int i = 0; i < MediaInfo.SubtitleStreams.Count; i++)
            {
                var s = MediaInfo.SubtitleStreams[i];
                var label = $"Track {i + 1}: {s.Codec}";
                if (s.Language is not null) label += $" ({s.Language})";
                if (s.Title is not null) label += $" — {s.Title}";
                SubtitleTrackLabels.Add(label);
            }

            // Auto-generate output path — unless the user wants to be asked where to save each
            // time, in which case leave it blank so Browse (the prompt) is required.
            if (string.IsNullOrWhiteSpace(OutputFile) && !_services.Settings.PromptForOutputLocation)
            {
                var dir = string.IsNullOrEmpty(_services.Settings.DefaultOutputDirectory)
                    ? Path.GetDirectoryName(InputFile)!
                    : _services.Settings.DefaultOutputDirectory;
                var name = Path.GetFileNameWithoutExtension(InputFile);
                var suffix = _services.Settings.DefaultOutputSuffix ?? string.Empty;
                OutputFile = Path.Combine(dir, $"{name}{suffix}{Container.GetExtension()}");
            }

            // Set preview max position from duration
            if (MediaInfo.Duration.TotalSeconds > 0)
            {
                PreviewMaxPosition = MediaInfo.Duration.TotalSeconds;
                PreviewPosition = MediaInfo.Duration.TotalSeconds * 0.1;
            }

            StatusMessage = $"Loaded: {MediaInfo.Video?.Width}x{MediaInfo.Video?.Height}, {MediaInfo.Duration:hh\\:mm\\:ss}";
            await UpdatePreview();
        }
        catch (OperationCanceledException) { /* Superseded by new analysis */ }
        catch (Exception ex)
        {
            MediaInfo = null;
            OnPropertyChanged(nameof(MediaInfoSummary));
            HasError = true;
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
    }

    // === Video Preview ===

    [RelayCommand]
    private async Task UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(InputFile) || MediaInfo is null) return;

        IsPreviewLoading = true;
        try
        {
            var pos = TimeSpan.FromSeconds(PreviewPosition);
            var framePath = await _services.PreviewService.ExtractFrameAsync(InputFile, pos);

            if (framePath is not null && File.Exists(framePath))
            {
                var oldImage = PreviewImage;
                var oldStream = _previewStream;
                // Load into MemoryStream so Bitmap doesn't reference a disposed FileStream
                var memoryStream = new MemoryStream();
                await using (var fileStream = File.OpenRead(framePath))
                {
                    await fileStream.CopyToAsync(memoryStream);
                }
                memoryStream.Seek(0, SeekOrigin.Begin);
                PreviewImage = new Bitmap(memoryStream);
                _previewStream = memoryStream;
                oldImage?.Dispose();
                oldStream?.Dispose();
                try { File.Delete(framePath); } catch { }
            }
        }
        catch
        {
            PreviewImage = null;
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    partial void OnPreviewPositionChanged(double value)
    {
        _ = UpdatePreviewDebounced();
    }

    partial void OnContainerChanged(ContainerFormat value)
    {
        UpdateOutputExtension();
    }

    partial void OnAudioOnlyChanged(bool value)
    {
        UpdateOutputExtension();
    }

    partial void OnSelectedAudioEncoderChanged(EncoderInfo? value)
    {
        UpdateOutputExtension();
    }

    private void UpdateOutputExtension()
    {
        if (string.IsNullOrWhiteSpace(OutputFile)) return;
        var dir = Path.GetDirectoryName(OutputFile) ?? "";
        var name = Path.GetFileNameWithoutExtension(OutputFile);
        var ext = AudioOnly ? GetAudioFileExtension(SelectedAudioEncoder?.Id) : Container.GetExtension();
        OutputFile = Path.Combine(dir, $"{name}{ext}");
    }

    private static string GetAudioFileExtension(string? encoderId)
        => EncodeJobBuilder.AudioFileExtension(encoderId);

    private CancellationTokenSource? _previewDebounce;
    private async Task UpdatePreviewDebounced()
    {
        _previewDebounce?.Cancel();
        _previewDebounce?.Dispose();
        _previewDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _previewDebounce.Token);
            await UpdatePreview();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Render a short snippet at the current scrub position through the FULL pipeline and show its
    /// first frame beside the source frame — a true before/after. On demand (not on every scrub)
    /// because it runs the real encoder. For a bigger, resizable comparison use "Full Preview".
    /// </summary>
    [RelayCommand]
    private async Task RenderOutputPreview()
    {
        if (string.IsNullOrWhiteSpace(InputFile)) { OutputPreviewStatus = "Load a video first."; return; }
        if (AudioOnly) { OutputPreviewStatus = "Audio-only mode has no video to preview."; return; }

        IsRenderingOutput = true;
        OutputRenderProgress = 0;
        OutputPreviewStatus = "Rendering through the full pipeline…";

        // Snapshot UI-thread state, then do ALL the work on a background thread. Building the
        // request calls GenerateScript, whose superres path runs a synchronous libplacebo probe
        // that can block for seconds — doing that (and the encode) here would freeze the window.
        // Progress is created on the UI thread so its callbacks marshal back automatically.
        var pos = PreviewPosition;
        var info = MediaInfo;
        var inputFile = InputFile;
        var outputFile = string.IsNullOrWhiteSpace(OutputFile) ? InputFile : OutputFile;
        var progress = new Progress<double>(p => OutputRenderProgress = p);
        try
        {
            var (snippetPath, stream, bitmap, duration) =
                await Task.Run<(string?, MemoryStream?, Bitmap?, double)>(async () =>
            {
                var request = BuildRequest(inputFile, outputFile);
                var result = await _services.SnippetPreview.RenderAsync(
                    request, info, TimeSpan.FromSeconds(pos), duration: null, progress);

                var framePath = await _services.PreviewService.ExtractFrameAsync(
                    result.SnippetPath, TimeSpan.Zero, maxWidth: 960);
                if (framePath is null)
                    return (result.SnippetPath, null, null, result.DurationSeconds);

                var ms = new MemoryStream();
                await using (var fs = File.OpenRead(framePath))
                    await fs.CopyToAsync(ms);
                ms.Seek(0, SeekOrigin.Begin);
                var bmp = new Bitmap(ms);
                try { File.Delete(framePath); } catch { }
                return (result.SnippetPath, ms, bmp, result.DurationSeconds);
            });

            // Back on the UI thread here (the command was invoked on it).
            _outputSnippetPath = snippetPath;
            if (bitmap is not null)
            {
                var oldImage = OutputPreviewImage;
                var oldStream = _outputPreviewStream;
                OutputPreviewImage = bitmap;
                _outputPreviewStream = stream;
                oldImage?.Dispose();
                oldStream?.Dispose();
            }
            OutputPreviewStatus = $"Output at {pos:F1}s (rendered {duration:F1}s snippet).";
        }
        catch (Exception ex)
        {
            OutputPreviewStatus = $"Preview failed: {ex.Message}";
        }
        finally
        {
            IsRenderingOutput = false;
        }
    }

    /// <summary>Play the last rendered output snippet in the system player (true motion).</summary>
    [RelayCommand]
    private void OpenOutputSnippet()
    {
        if (_outputSnippetPath is null || !File.Exists(_outputSnippetPath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = _outputSnippetPath, UseShellExecute = true }); }
        catch (Exception ex) { OutputPreviewStatus = $"Couldn't open the player: {ex.Message}"; }
    }

    // === Profiles ===

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _services.ProfileManager.GetAllProfiles())
            Profiles.Add(p);
    }

    /// <summary>Suppresses <see cref="ApplyProfile"/> while re-selecting a just-saved profile,
    /// so an in-place Save/Update doesn't tear down and rebuild the current form state.</summary>
    private bool _suppressProfileApply;

    partial void OnSelectedProfileChanged(EncodingProfile? value)
    {
        if (value is null || _suppressProfileApply) return;
        ApplyProfile(value);
    }

    public void ApplyProfile(EncodingProfile profile)
    {
        SelectedVideoEncoder = VideoEncoders.FirstOrDefault(e => e.Id == profile.VideoEncoderId)
                               ?? VideoEncoders.FirstOrDefault();
        EncodingMode = profile.VideoMode;
        Quality = profile.VideoQuality;
        Bitrate = profile.VideoBitrate;
        Preset = profile.VideoPreset;
        Tune = profile.VideoTune;
        SvtAv1Preset = profile.SvtAv1Preset;
        CustomVideoArgs = profile.VideoCustomArgs;
        BitDepth = profile.BitDepth;
        AiUpscaleModel = profile.AiUpscaleModel;
        _aiUpscaleHeight = profile.AiUpscaleHeight;

        SelectedAudioEncoder = AudioEncoders.FirstOrDefault(e => e.Id == profile.AudioEncoderId)
                               ?? AudioEncoders.FirstOrDefault();
        AudioBitrateMode = profile.AudioBitrateMode;
        AudioBitrate = profile.AudioBitrate;
        CustomAudioArgs = profile.AudioCustomArgs;
        AudioChannelMode = profile.AudioChannelMode;
        AudioSampleRate = profile.AudioSampleRate;
        AudioNormalization = profile.AudioNormalization;
        NormalizationTarget = profile.NormalizationTarget;
        TruePeakLimit = profile.TruePeakLimit;
        AudioSampleFormat = profile.AudioSampleFormat;
        AudioOnly = profile.AudioOnly;

        Container = profile.Container;

        // Restore filter chain if the profile carries one — into BOTH the chain model and the
        // Filters-tab UI list (LoadChain keeps them in sync; the old code populated only the
        // model, so profile filters were invisible and uneditable in the Filters tab). Guarded on
        // the profile actually having filters, so applying a codec-only profile leaves any
        // existing chain untouched.
        if (_filterTab is not null && profile.FilterEngineId is not null && profile.Filters.Count > 0)
            _filterTab.LoadChain(profile.FilterEngineId,
                profile.Filters.Select(pf => (pf.FilterId, pf.Parameters)).ToList());
    }

    /// <summary>
    /// Captures current encode settings as a profile (for batch per-file restore).
    /// </summary>
    public EncodingProfile CaptureCurrentSettings() => new()
    {
        Name = "__snapshot__",
        VideoEncoderId = SelectedVideoEncoder?.Id ?? "ffmpeg-x265",
        VideoMode = EncodingMode,
        VideoQuality = Quality,
        VideoBitrate = Bitrate,
        VideoPreset = Preset,
        VideoTune = Tune,
        SvtAv1Preset = SvtAv1Preset,
        VideoCustomArgs = CustomVideoArgs,
        BitDepth = BitDepth,
        AiUpscaleModel = AiUpscaleModel,
        AiUpscaleHeight = _aiUpscaleHeight,
        AudioEncoderId = SelectedAudioEncoder?.Id ?? "ffmpeg-audio-opus",
        AudioBitrateMode = AudioBitrateMode,
        AudioBitrate = AudioBitrate,
        AudioCustomArgs = CustomAudioArgs,
        AudioChannelMode = AudioChannelMode,
        AudioSampleRate = AudioSampleRate,
        AudioNormalization = AudioNormalization,
        NormalizationTarget = NormalizationTarget,
        TruePeakLimit = TruePeakLimit,
        AudioSampleFormat = AudioSampleFormat,
        AudioOnly = AudioOnly,
        Container = Container,
    };

    /// <summary>
    /// Build an <see cref="EncodingProfile"/> from the current UI state. When overwriting an
    /// existing profile, pass it as <paramref name="basedOn"/> so metadata the form doesn't
    /// expose (Description, CreatedAt) survives instead of being reset. AiUpscaleHeight is
    /// tracked on the ViewModel (<see cref="_aiUpscaleHeight"/>) so it round-trips with the model.
    /// </summary>
    private EncodingProfile BuildProfile(string name, EncodingProfile? basedOn = null) => new()
    {
        Name = name,
        Description = basedOn?.Description ?? string.Empty,
        CreatedAt = basedOn?.CreatedAt ?? DateTime.UtcNow,
        AiUpscaleHeight = _aiUpscaleHeight,
        VideoEncoderId = SelectedVideoEncoder?.Id ?? "ffmpeg-x265",
        VideoMode = EncodingMode,
        VideoQuality = Quality,
        VideoBitrate = Bitrate,
        VideoPreset = Preset,
        VideoTune = Tune,
        SvtAv1Preset = SvtAv1Preset,
        VideoCustomArgs = CustomVideoArgs,
        BitDepth = BitDepth,
        AiUpscaleModel = AiUpscaleModel,
        AudioEncoderId = SelectedAudioEncoder?.Id ?? "ffmpeg-audio-opus",
        AudioBitrateMode = AudioBitrateMode,
        AudioBitrate = AudioBitrate,
        AudioCustomArgs = CustomAudioArgs,
        AudioChannelMode = AudioChannelMode,
        AudioSampleRate = AudioSampleRate,
        AudioNormalization = AudioNormalization,
        NormalizationTarget = NormalizationTarget,
        TruePeakLimit = TruePeakLimit,
        AudioSampleFormat = AudioSampleFormat,
        AudioOnly = AudioOnly,
        Container = Container,
        FilterEngineId = _filterTab?.SelectedEngine?.Id,
        Filters = _filterTab?.Chain.Filters.Select(af => new ProfileFilter
        {
            FilterId = af.Filter.Id,
            Parameters = new Dictionary<string, object>(af.ParameterValues),
        }).ToList() ?? [],
    };

    /// <summary>Re-select a profile by name without re-applying it to the form.</summary>
    private void ReselectProfile(string name)
    {
        _suppressProfileApply = true;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == name);
        _suppressProfileApply = false;
    }

    [RelayCommand]
    private void SaveProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        // Preserve description/metadata when "Save As..." overwrites an existing name.
        var existing = Profiles.FirstOrDefault(p => p.Name == profileName);
        try
        {
            _services.ProfileManager.Save(BuildProfile(profileName, existing));
        }
        catch (Exception ex)
        {
            // ProfileManager.Save writes atomically but still throws on failure (bad path,
            // full disk). Surface it in the UI instead of letting it escape onto the UI thread.
            HasError = true;
            StatusMessage = $"Couldn't save profile '{profileName}': {ex.Message}";
            return;
        }
        RefreshProfiles();
        ReselectProfile(profileName);
        HasError = false;
        StatusMessage = $"Saved profile '{profileName}'.";
    }

    /// <summary>
    /// Write the current settings back into the selected profile — the in-place "edit" action.
    /// Overwrites the profile's JSON (which is what wins on disk, so it persists across restarts,
    /// including for built-ins).
    /// </summary>
    [RelayCommand]
    private void UpdateProfile()
    {
        if (SelectedProfile is null) return;
        var name = SelectedProfile.Name;
        try
        {
            _services.ProfileManager.Save(BuildProfile(name, SelectedProfile));
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Couldn't update profile '{name}': {ex.Message}";
            return;
        }
        RefreshProfiles();
        ReselectProfile(name);
        HasError = false;
        StatusMessage = $"Updated profile '{name}'.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var name = SelectedProfile.Name;
        try
        {
            _services.ProfileManager.Delete(name);
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Couldn't delete profile '{name}': {ex.Message}";
            return;
        }
        SelectedProfile = null;
        RefreshProfiles();
        StatusMessage = $"Deleted profile '{name}'.";
    }

    // === Add to Queue ===

    /// <summary>
    /// Raised after a successful enqueue from the Encode tab so the shell can surface the
    /// Queue tab. Not fired from the shared <see cref="EnqueueFile"/> because batch enqueues
    /// must not hijack navigation.
    /// </summary>
    public Action? JobQueued { get; set; }

    [RelayCommand]
    private async Task AddToQueue()
    {
        if (string.IsNullOrWhiteSpace(InputFile) || string.IsNullOrWhiteSpace(OutputFile))
        {
            HasError = true;
            StatusMessage = "Input and output files must be specified.";
            return;
        }

        if (!File.Exists(InputFile))
        {
            HasError = true;
            StatusMessage = $"Input file not found: {InputFile}";
            return;
        }

        try
        {
            _ = Path.GetFullPath(OutputFile);
        }
        catch
        {
            HasError = true;
            StatusMessage = $"Invalid output path: {OutputFile}";
            return;
        }

        // Validate output directory is writable
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(OutputFile));
        if (!string.IsNullOrEmpty(outputDir))
        {
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception ex)
            {
                HasError = true;
                StatusMessage = $"Cannot create output directory: {ex.Message}";
                return;
            }
        }

        if (!AudioOnly && SelectedVideoEncoder is null)
        {
            HasError = true;
            StatusMessage = "No video encoder selected.";
            return;
        }

        if (SelectedAudioEncoder is null)
        {
            HasError = true;
            StatusMessage = "No audio encoder selected.";
            return;
        }

        if (MediaInfo is null)
        {
            HasError = true;
            StatusMessage = "File not analyzed yet. Click 'Analyze' or drop a file first.";
            return;
        }

        // Avoid overwriting existing files — auto-increment filename
        var actualOutput = GetUniqueOutputPath(OutputFile);
        if (actualOutput != OutputFile)
        {
            OutputFile = actualOutput;
            StatusMessage = $"Output renamed to avoid overwrite: {Path.GetFileName(actualOutput)}";
        }
        else
        {
            StatusMessage = "Added to queue.";
        }

        // Guard: the chosen codecs must actually fit in the chosen container, otherwise
        // the encodes run to completion and only the final mux fails.
        var incompatible = EncodeJobBuilder.Validate(BuildRequest(InputFile, OutputFile), MediaInfo);
        if (incompatible is not null)
        {
            HasError = true;
            StatusMessage = incompatible;
            return;
        }

        // Guard: AviSynth chains still need avs2pipe piping that isn't built. VapourSynth IS wired now
        // (VapourSynthPipeProcessor runs vspipe -> ffmpeg), so only engines other than FFmpeg and
        // VapourSynth are rejected here.
        if (!AudioOnly && _filterTab?.SelectedEngine is not null
            && _filterTab.Chain.Filters.Count > 0
            && _filterTab.SelectedEngine.Id is not ("ffmpeg" or "vapoursynth"))
        {
            HasError = true;
            StatusMessage = $"{_filterTab.SelectedEngine.Name} filter chains cannot be encoded yet — " +
                            "use the FFmpeg or VapourSynth engine, or clear the filter chain to encode unfiltered.";
            return;
        }

        HasError = false;

        // Surface anything that will be quietly lost, before it is.
        var warnings = EncodeJobBuilder.Warnings(
            BuildRequest(InputFile, OutputFile), MediaInfo, UseChunkedEncoding);
        EncodeWarnings = warnings.Count > 0 ? string.Join("\n", warnings.Select(w => "⚠ " + w)) : string.Empty;

        // Chunking needs a planning pass (scene detection) before any job exists.
        ChunkedEncodePreparation? chunks = null;
        if (UseChunkedEncoding && !AudioOnly && MediaInfo is not null)
        {
            try
            {
                StatusMessage = "Planning chunks…";
                var coordinator = new ChunkedEncode(_services, _services.LoggerFactory);
                coordinator.DetectionProgress += p => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => StatusMessage = $"Detecting scenes… {p * 100:F0}%");

                var request = BuildRequest(InputFile, OutputFile);
                var chunkOptions = BuildChunkingOptions();
                var chunkPlan = await coordinator.PlanAsync(request, MediaInfo, chunkOptions);

                if (chunkPlan is null)
                {
                    StatusMessage = "Too short to be worth chunking; queued as a single encode.";
                }
                else
                {
                    // Per-scene assignments feed into the chunk directory name, so they
                    // have to be settled before the chunk files are named.
                    IReadOnlyDictionary<int, double>? perScene = null;
                    if (UsePerSceneQuality)
                    {
                        var optimizer = new PerSceneOptimizer(_services, _services.LoggerFactory);
                        optimizer.Progress += (done, total, message) =>
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusMessage = message);

                        var perSceneResult = await optimizer.OptimizeAsync(
                            request, chunkPlan,
                            new PerSceneOptions { Metric = TargetMetric, Target = TargetScore },
                            MediaInfo);

                        perScene = perSceneResult.ChunkQuality;
                        QualitySearchStatus = "Per-scene: " + perSceneResult.Summary;
                    }

                    chunks = coordinator.Prepare(request, chunkPlan, chunkOptions, perScene);
                    StatusMessage = $"Queued as {chunks.Plan.Chunks.Count} chunks" +
                        (chunks.AlreadyDone.Count > 0 ? $" ({chunks.AlreadyDone.Count} reused)" : "") + ".";
                }
            }
            catch (Exception ex)
            {
                HasError = true;
                StatusMessage = $"Could not plan chunks: {ex.Message}";
                return;
            }
        }

        EnqueueFile(InputFile, OutputFile, chunks: chunks);

        // Surface the Queue tab so the freshly-added job is visible, rather than leaving the
        // user on the Encode tab wondering whether anything happened.
        JobQueued?.Invoke();
    }

    private ChunkingOptions BuildChunkingOptions() => new()
    {
        Mode = ChunkSceneDetection ? ChunkMode.Scene : ChunkMode.Fixed,
        MaxChunkDuration = TimeSpan.FromSeconds(Math.Max(2, MaxChunkSeconds)),
    };

    private static string GetUniqueOutputPath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return path; // give up after 999 attempts
    }

    /// <summary>
    /// Enqueue a file and return ALL sub-job IDs (video, audio, mux) for comprehensive tracking.
    /// </summary>
    public List<string> EnqueueFileWithAllIds(string inputFile, string outputFile, MediaFileInfo? mediaInfo = null)
    {
        var ids = new List<string>();
        var originalEnqueue = EnqueueFile(inputFile, outputFile, mediaInfo, ids);
        return ids;
    }

    /// <summary>
    /// Enqueue a file for encoding. Returns the final job ID (mux or audio-only) for tracking.
    /// When called from batch, pass mediaInfo directly to avoid mutating shared EncodeTab state.
    /// </summary>
    public string EnqueueFile(
        string inputFile, string outputFile, MediaFileInfo? mediaInfo = null,
        List<string>? allJobIds = null, ChunkedEncodePreparation? chunks = null)
    {
        // Use provided mediaInfo (batch mode) or fall back to shared MediaInfo (single encode)
        var info = mediaInfo ?? MediaInfo;

        var request = BuildRequest(inputFile, outputFile);

        // Guard here — the single place that constructs the enqueued request — so both the
        // single-file (AddToQueue) and batch (EnqueueFileWithAllIds) paths reject incompatible
        // codec/container pairs up front instead of running every encode to completion only for
        // the final mux to fail. AddToQueue also validates earlier for its inline HasError UX;
        // this is the backstop that covers batch, which otherwise skipped Validate entirely.
        var invalid = EncodeJobBuilder.Validate(request, info);
        if (invalid is not null)
            throw new InvalidOperationException(invalid);

        var plan = EncodeJobBuilder.Build(
            request, info, chunks, ffprobePath: _services.Settings.FFprobePath);

        foreach (var job in plan.AllJobs)
        {
            _services.JobQueue.Enqueue(job);
            allJobIds?.Add(job.Id);
        }

        // Remember it, so an interruption doesn't lose the queue. The entry clears
        // itself when the final job settles.
        _services.QueueStore.Record(
            request,
            plan.FinalJob.Id,
            chunks is not null ? BuildChunkingOptions() : null,
            chunks?.PerChunkQuality);

        return plan.FinalJob.Id;
    }

    /// <summary>The composition root, for views that need to open a secondary window (e.g. Preview).</summary>
    public AppServices Services => _services;

    /// <summary>
    /// The current UI state as an <see cref="EncodeRequest"/> for previewing, or null if no input
    /// is loaded. Uses the SAME <see cref="BuildRequest"/> the encode uses, so a preview reflects
    /// exactly the settings that would be applied.
    /// </summary>
    public EncodeRequest? BuildPreviewRequest() =>
        string.IsNullOrWhiteSpace(InputFile)
            ? null
            : BuildRequest(InputFile, string.IsNullOrWhiteSpace(OutputFile) ? InputFile : OutputFile);

    /// <summary>
    /// Snapshot the current UI state as an <see cref="EncodeRequest"/>. The GUI and the CLI
    /// both feed this into <see cref="EncodeJobBuilder"/>, so they build identical pipelines.
    /// </summary>
    private EncodeRequest BuildRequest(string inputFile, string outputFile)
    {
        string? filterScript = null;
        if (!AudioOnly && _filterTab?.SelectedEngine is not null && _filterTab.Chain.Filters.Count > 0)
            filterScript = _filterTab.SelectedEngine.GenerateScript(_filterTab.Chain, inputFile);

        return new EncodeRequest
        {
            InputFile = inputFile,
            OutputFile = outputFile,
            TempDirectory = _services.Settings.TempDirectory,

            VideoEncoderId = SelectedVideoEncoder?.Id ?? "ffmpeg-x265",
            Mode = EncodingMode,
            Quality = Quality,
            Bitrate = Bitrate,
            Preset = Preset,
            Tune = Tune,
            SvtAv1Preset = SvtAv1Preset,
            X265StreamWrap = X265StreamWrap,
            SvtAv1Tune = SvtAv1Tune,
            SvtAv1FilmGrain = SvtAv1FilmGrain,
            SvtAv1TileRows = SvtAv1TileRows,
            SvtAv1TileColumns = SvtAv1TileColumns,
            SvtAv1KeyframeInterval = SvtAv1KeyframeInterval,
            VideoCustomArgs = CustomVideoArgs,

            AudioEncoderId = SelectedAudioEncoder?.Id ?? "ffmpeg-audio-opus",
            AudioBitrateMode = AudioBitrateMode,
            AudioBitrate = AudioBitrate,
            AudioChannelMode = AudioChannelMode,
            AudioSampleRate = AudioSampleRate,
            AudioSampleFormat = AudioSampleFormat,
            AudioNormalization = AudioNormalization,
            NormalizationTarget = NormalizationTarget,
            TruePeakLimit = TruePeakLimit,
            AudioCustomArgs = CustomAudioArgs,
            AudioTracks = ResolveAudioTracks(),

            OpusComplexity = OpusComplexity,
            AacProfile = AacProfile,
            Mp3VbrScale = Mp3VbrScale,
            FlacCompressionLevel = FlacCompressionLevel,
            Ac3DialogueNorm = Ac3DialogueNorm,
            Eac3DialogueNorm = Eac3DialogueNorm,
            VorbisQuality = VorbisQuality,

            Container = Container,
            AudioOnly = AudioOnly,
            CopySubtitles = CopySubtitles,
            ExternalSubtitleFile = ExternalSubtitleFile,
            SubtitleTracks = ParseSubtitleTracks(),
            CopyChapters = CopyChapters,
            CopyAttachments = CopyAttachments,
            CopyMetadata = CopyMetadata,
            BitDepth = BitDepth,
            Chroma = Chroma,
            AiUpscaleModel = AiUpscaleModel,
            AiUpscaleHeight = _aiUpscaleHeight,
            AiUpscaleAllowLong = AiUpscaleAllowLong,
            RifeTargetFps = RifeTargetFps,
            BurnInSubtitles = BurnInSubtitles,
            BurnInSubtitleTrack = BurnInSubtitleTrack,
            PreserveDynamicHdr = PreserveDynamicHdr,

            FilterScript = filterScript,
            FilterEngineId = _filterTab?.SelectedEngine?.Id,
        };
    }

    /// <summary>
    /// Parse the comma-separated subtitle-track field into stream indices. Blank/garbage
    /// yields an empty list, which <see cref="EncodeJobBuilder"/> treats as "keep all".
    /// </summary>
    private IReadOnlyList<int> ParseSubtitleTracks()
    {
        if (string.IsNullOrWhiteSpace(SubtitleTracks)) return [];
        return SubtitleTracks
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Which source audio streams to encode. "All tracks" needs the analysed source to
    /// know how many there are; without it we fall back to the selected one.
    /// </summary>
    private IReadOnlyList<int> ResolveAudioTracks()
    {
        if (!EncodeAllAudioTracks) return [SelectedAudioTrackIndex];

        var count = MediaInfo?.AudioStreams.Count ?? 0;
        return count > 1 ? Enumerable.Range(0, count).ToList() : [SelectedAudioTrackIndex];
    }

    public void Dispose()
    {
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _previewDebounce?.Cancel();
        _previewDebounce?.Dispose();
        _previewDebounce = null;
        PreviewImage?.Dispose();
        PreviewImage = null;
        _previewStream?.Dispose();
        _previewStream = null;
        OutputPreviewImage?.Dispose();
        OutputPreviewImage = null;
        _outputPreviewStream?.Dispose();
        _outputPreviewStream = null;
    }
}
