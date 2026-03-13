using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.App.Services;
using Moist.Core.Encoders;
using Moist.Core.Encoders.Audio;
using Moist.Core.Encoders.Video;
using Moist.Core.Jobs;
using Moist.Core.Media;
using Moist.Core.Muxers;
using Moist.Core.Profiles;

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

    // Subtitle options
    [ObservableProperty] private bool _copySubtitles = true;
    [ObservableProperty] private string _externalSubtitleFile = string.Empty;
    public ObservableCollection<string> SubtitleTrackLabels { get; } = [];

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

        foreach (var enc in services.Encoders.GetVideoEncoders())
            VideoEncoders.Add(enc);
        foreach (var enc in services.Encoders.GetAudioEncoders())
            AudioEncoders.Add(enc);

        SelectedVideoEncoder = VideoEncoders.FirstOrDefault();
        SelectedAudioEncoder = AudioEncoders.FirstOrDefault();

        RefreshProfiles();
    }

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

    private static string GetAudioFileExtension(string? encoderId) => encoderId switch
    {
        "ffmpeg-audio-opus" => ".opus",
        "ffmpeg-audio-aac" => ".m4a",
        "ffmpeg-audio-mp3" => ".mp3",
        "ffmpeg-audio-flac" => ".flac",
        "ffmpeg-audio-ac3" => ".ac3",
        "ffmpeg-audio-eac3" => ".eac3",
        "ffmpeg-audio-vorbis" => ".ogg",
        "ffmpeg-audio-copy" => ".mka",
        _ => ".mka"
    };

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
    private void AddToQueue()
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

        HasError = false;
        EnqueueFile(InputFile, OutputFile);
    }

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
    /// Enqueue a file for encoding. Returns the mux job ID for tracking.
    /// When called from batch, pass mediaInfo directly to avoid mutating shared EncodeTab state.
    /// </summary>
    public string EnqueueFile(string inputFile, string outputFile, MediaFileInfo? mediaInfo = null)
    {
        // Use provided mediaInfo (batch mode) or fall back to shared MediaInfo (single encode)
        var info = mediaInfo ?? MediaInfo;

        // Ensure temp and output directories exist
        Directory.CreateDirectory(_services.Settings.TempDirectory);
        var outputDir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        // Audio-only mode skips video entirely
        VideoJob? videoJob = null;
        string? tempVideoFile = null;

        if (!AudioOnly)
        {
            var encoderId = SelectedVideoEncoder?.Id ?? "ffmpeg-x265";

            VideoEncoderSettings videoSettings = encoderId switch
            {
                "ffmpeg-x264" or "cli-x264" => new X264Settings(),
                "ffmpeg-x265" or "cli-x265" => new X265Settings(),
                "ffmpeg-svtav1" => new SvtAv1Settings { SvtPreset = SvtAv1Preset },
                _ => new VideoEncoderSettings()
            };
            videoSettings.Mode = EncodingMode;
            videoSettings.Quality = Quality;
            videoSettings.Bitrate = Bitrate;
            videoSettings.Preset = Preset;
            videoSettings.Tune = Tune;
            videoSettings.CustomArgs = CustomVideoArgs;

            string? filterScript = null;
            if (_filterTab?.SelectedEngine is not null && _filterTab.Chain.Filters.Count > 0)
                filterScript = _filterTab.SelectedEngine.GenerateScript(_filterTab.Chain, inputFile);

            tempVideoFile = Path.Combine(
                _services.Settings.TempDirectory,
                $"moist_v_{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(outputFile)}.mkv");

            videoJob = new VideoJob
            {
                Name = $"Video: {Path.GetFileName(inputFile)}",
                InputFile = inputFile,
                OutputFile = tempVideoFile,
                EncoderId = encoderId,
                Settings = videoSettings,
                FilterScript = filterScript,
                TotalFrames = info?.Video?.TotalFrames ?? 0,
            };
        }

        var tempAudioFile = Path.Combine(
            _services.Settings.TempDirectory,
            $"moist_a_{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(outputFile)}.mka");

        var audioEncoderId = SelectedAudioEncoder?.Id ?? "ffmpeg-audio-opus";
        var audioSettings = CreateAudioSettings(audioEncoderId);

        var audioJob = new AudioJob
        {
            Name = $"Audio: {Path.GetFileName(inputFile)}",
            InputFile = inputFile,
            OutputFile = tempAudioFile,
            EncoderId = audioEncoderId,
            Settings = audioSettings,
            TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
            AudioStreamIndex = SelectedAudioTrackIndex,
        };

        // Audio-only mode: encode audio directly to output, no video or mux
        if (AudioOnly)
        {
            audioJob.OutputFile = outputFile;
            _services.JobQueue.Enqueue(audioJob);
            return audioJob.Id;
        }

        // Build subtitle inputs if CopySubtitles is enabled
        var subtitleInputs = new List<SubtitleTrackInput>();
        if (CopySubtitles && info?.SubtitleStreams is { Count: > 0 } subs)
        {
            foreach (var sub in subs)
            {
                subtitleInputs.Add(new SubtitleTrackInput
                {
                    FilePath = inputFile,
                    Language = sub.Language,
                    TrackName = sub.Title,
                    IsDefault = sub.IsDefault,
                    IsForced = sub.IsForced,
                });
            }
        }

        // Add external subtitle file if specified
        if (!string.IsNullOrWhiteSpace(ExternalSubtitleFile) && File.Exists(ExternalSubtitleFile))
        {
            subtitleInputs.Add(new SubtitleTrackInput
            {
                FilePath = ExternalSubtitleFile,
                IsDefault = subtitleInputs.Count == 0,
            });
        }

        var dependsOn = new List<string>();
        if (videoJob is not null) dependsOn.Add(videoJob.Id);
        dependsOn.Add(audioJob.Id);

        var muxJob = new MuxJob
        {
            Name = $"Mux: {Path.GetFileName(outputFile)}",
            InputFile = inputFile,
            OutputFile = outputFile,
            MuxerId = "ffmpeg-mux",
            Container = Container,
            VideoInput = tempVideoFile,
            AudioInputs = [new AudioTrackInput { FilePath = tempAudioFile }],
            SubtitleInputs = subtitleInputs,
            DependsOn = dependsOn,
            TotalDurationSeconds = info?.Duration.TotalSeconds ?? 0,
        };

        if (tempVideoFile is not null) muxJob.FilesToCleanup.Add(tempVideoFile);
        muxJob.FilesToCleanup.Add(tempAudioFile);

        if (videoJob is not null) _services.JobQueue.Enqueue(videoJob);
        _services.JobQueue.Enqueue(audioJob);
        _services.JobQueue.Enqueue(muxJob);

        return muxJob.Id;
    }

    private AudioEncoderSettings CreateAudioSettings(string audioEncoderId)
    {
        AudioEncoderSettings s = audioEncoderId switch
        {
            "ffmpeg-audio-opus" => new OpusSettings { Complexity = OpusComplexity },
            "ffmpeg-audio-aac" => new AacSettings { Profile = string.IsNullOrEmpty(AacProfile) ? null : AacProfile },
            "ffmpeg-audio-mp3" => new Mp3Settings { VbrScale = Mp3VbrScale },
            "ffmpeg-audio-flac" => new FlacSettings { CompressionLevel = FlacCompressionLevel },
            "ffmpeg-audio-ac3" => new Ac3Settings { DialogueNormalization = Ac3DialogueNorm },
            "ffmpeg-audio-eac3" => new Eac3Settings { DialogueNormalization = Eac3DialogueNorm },
            "ffmpeg-audio-vorbis" => new VorbisSettings { QualityScale = VorbisQuality },
            _ => new AudioEncoderSettings()
        };

        s.BitrateMode = AudioBitrateMode;
        s.Bitrate = AudioBitrate;
        s.ChannelMode = AudioChannelMode;
        s.SampleRate = AudioSampleRate > 0 ? AudioSampleRate : null;
        s.CustomArgs = CustomAudioArgs;
        s.Normalization = AudioNormalization;
        s.NormalizationTarget = NormalizationTarget;
        s.TruePeakLimit = TruePeakLimit;
        s.SampleFormat = AudioSampleFormat;
        return s;
    }

    public void Dispose()
    {
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = null;
        _previewDebounce?.Cancel();
        _previewDebounce?.Dispose();
        _previewDebounce = null;
        PreviewImage?.Dispose();
        PreviewImage = null;
        _previewStream?.Dispose();
        _previewStream = null;
    }
}
