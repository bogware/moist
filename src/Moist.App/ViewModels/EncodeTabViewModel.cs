using System.Collections.ObjectModel;
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
    [ObservableProperty] private VideoEncodingMode _encodingMode = VideoEncodingMode.ConstantRateFactor;
    [ObservableProperty] private double _quality = 23.0;
    [ObservableProperty] private int _bitrate = 5000;
    [ObservableProperty] private VideoPreset _preset = VideoPreset.Medium;
    [ObservableProperty] private VideoTune _tune = VideoTune.None;
    [ObservableProperty] private int _svtAv1Preset = 6;
    [ObservableProperty] private string _customVideoArgs = string.Empty;

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
    public ObservableCollection<string> SubtitleTrackLabels { get; } = [];

    // Video bit depth and chroma
    [ObservableProperty] private VideoBitDepth _bitDepth = VideoBitDepth.Auto;
    public static VideoBitDepth[] BitDepthOptions { get; } = Enum.GetValues<VideoBitDepth>();

    [ObservableProperty] private ChromaSubsampling _chroma = ChromaSubsampling.Auto;
    public static ChromaSubsampling[] ChromaOptions { get; } = Enum.GetValues<ChromaSubsampling>();

    /// <summary>Render subtitles into the picture rather than muxing them as a track.</summary>
    [ObservableProperty] private bool _burnInSubtitles;

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

            // Auto-generate output path
            if (string.IsNullOrWhiteSpace(OutputFile))
            {
                var dir = string.IsNullOrEmpty(_services.Settings.DefaultOutputDirectory)
                    ? Path.GetDirectoryName(InputFile)!
                    : _services.Settings.DefaultOutputDirectory;
                var name = Path.GetFileNameWithoutExtension(InputFile);
                OutputFile = Path.Combine(dir, $"{name}_encoded{Container.GetExtension()}");
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

    // === Profiles ===

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _services.ProfileManager.GetAllProfiles())
            Profiles.Add(p);
    }

    partial void OnSelectedProfileChanged(EncodingProfile? value)
    {
        if (value is null) return;
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

        // Restore filter chain if profile has one
        if (_filterTab is not null && profile.Filters.Count > 0 && profile.FilterEngineId is not null)
        {
            var engine = _filterTab.Engines.FirstOrDefault(e => e.Id == profile.FilterEngineId);
            if (engine is not null)
            {
                _filterTab.SelectedEngine = engine;
                _filterTab.ClearFiltersCommand.Execute(null);
                var engineFilters = engine.GetAvailableFilters();
                foreach (var pf in profile.Filters)
                {
                    var filterDef = engineFilters.FirstOrDefault(f => f.Id == pf.FilterId);
                    if (filterDef is not null)
                    {
                        _filterTab.Chain.Add(filterDef, new Dictionary<string, object>(pf.Parameters));
                    }
                }
            }
        }
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

    [RelayCommand]
    private void SaveProfile(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        var profile = new EncodingProfile
        {
            Name = profileName,
            VideoEncoderId = SelectedVideoEncoder?.Id ?? "ffmpeg-x265",
            VideoMode = EncodingMode,
            VideoQuality = Quality,
            VideoBitrate = Bitrate,
            VideoPreset = Preset,
            VideoTune = Tune,
            SvtAv1Preset = SvtAv1Preset,
            VideoCustomArgs = CustomVideoArgs,
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

        _services.ProfileManager.Save(profile);
        RefreshProfiles();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        _services.ProfileManager.Delete(SelectedProfile.Name);
        SelectedProfile = null;
        RefreshProfiles();
    }

    // === Add to Queue ===

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

        // Guard: non-FFmpeg filter engines can't be used with FFmpeg-based encoders
        if (!AudioOnly && _filterTab?.SelectedEngine is not null
            && _filterTab.Chain.Filters.Count > 0
            && _filterTab.SelectedEngine.Id != "ffmpeg")
        {
            HasError = true;
            StatusMessage = $"The {_filterTab.SelectedEngine.Name} filter engine requires standalone CLI encoders (x264/x265 CLI with vspipe piping). " +
                            "Please switch to FFmpeg Filters or clear the filter chain.";
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
            CopyChapters = CopyChapters,
            CopyAttachments = CopyAttachments,
            BitDepth = BitDepth,
            Chroma = Chroma,
            BurnInSubtitles = BurnInSubtitles,

            FilterScript = filterScript,
            FilterEngineId = _filterTab?.SelectedEngine?.Id,
        };
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
    }
}
