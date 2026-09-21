using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Services;
using Moist.Core.Jobs;
using Moist.Core.Muxers;
using Moist.Core.Profiles;

namespace Moist.App.ViewModels;

public partial class BatchTabViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly EncodeTabViewModel _encodeTab;
    private readonly Dictionary<string, BatchItemViewModel> _jobToItem = new();

    public ObservableCollection<BatchItemViewModel> Items { get; } = [];
    public ObservableCollection<string> AvailableProfiles { get; } = [];

    [ObservableProperty] private string _outputDirectory = string.Empty;
    [ObservableProperty] private string _outputSuffix = "_encoded";
    [ObservableProperty] private bool _useSourceDirectory = true;

    public BatchTabViewModel(AppServices services, EncodeTabViewModel encodeTab)
    {
        _services = services;
        _encodeTab = encodeTab;
        _outputDirectory = services.Settings.DefaultOutputDirectory;

        // Load available profile names for per-file override
        LoadProfiles();

        // Track job status changes for batch items — map all sub-jobs to the batch item
        services.JobQueue.JobStatusChanged += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_jobToItem.TryGetValue(job.Id, out var item))
                {
                    // Show the most informative status across all sub-jobs
                    if (job.Status == JobStatus.Failed)
                        item.Status = $"Failed: {job.ErrorMessage}";
                    else if (job.Status == JobStatus.Cancelled)
                        item.Status = "Cancelled";
                    else if (job.Status == JobStatus.Running)
                        item.Status = $"Encoding ({job.JobType})";
                    else if (job.Status == JobStatus.Completed && job is MuxJob)
                        item.Status = "Completed";  // Only mark completed when the final mux is done
                    else if (job.Status == JobStatus.Completed && job is AudioJob && _encodeTab.AudioOnly)
                        item.Status = "Completed";  // Audio-only: no mux
                }
            });
        };

        services.JobQueue.JobProgressUpdated += (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_jobToItem.TryGetValue(args.Job.Id, out var item))
                {
                    item.Progress = args.Progress.Percentage;
                }
            });
        };
    }

    // Keep derived output paths in step with the suffix / target-directory controls instead of
    // requiring a manual "Refresh Paths" click. Field initializers and the ctor assign the
    // backing fields directly, so these don't fire (harmlessly) during construction.
    partial void OnOutputSuffixChanged(string value) => RefreshOutputPaths();
    partial void OnUseSourceDirectoryChanged(bool value) => RefreshOutputPaths();
    partial void OnOutputDirectoryChanged(string value)
    {
        if (!UseSourceDirectory) RefreshOutputPaths();
    }

    /// <summary>
    /// The extension the encode will actually produce, mirroring the Encode tab's own logic:
    /// audio-only mode writes the AudioJob straight to the output with the audio codec's
    /// extension (EncodeJobBuilder.AudioFileExtension), everything else uses the container's.
    /// Deriving the batch filename from the container alone mislabels audio-only outputs and
    /// (once a per-file profile overrides the container) container-mismatched ones.
    /// </summary>
    private string OutputExtension() =>
        _encodeTab.AudioOnly
            ? EncodeJobBuilder.AudioFileExtension(_encodeTab.SelectedAudioEncoder?.Id)
            : _encodeTab.Container.GetExtension();

    public void HandleDroppedFiles(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            if (Items.Any(i => i.InputFile == path)) continue;

            var outputDir = UseSourceDirectory
                ? Path.GetDirectoryName(path) ?? ""
                : OutputDirectory;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = OutputExtension();

            Items.Add(new BatchItemViewModel
            {
                InputFile = path,
                OutputFile = Path.Combine(outputDir, $"{name}{OutputSuffix}{ext}"),
                Status = "Pending"
            });
        }
    }

    [RelayCommand]
    private void RemoveItem(BatchItemViewModel? item)
    {
        if (item is not null)
            Items.Remove(item);
    }

    [RelayCommand]
    private void ClearAll()
    {
        Items.Clear();
    }

    [RelayCommand]
    private async Task AddAllToQueue()
    {
        foreach (var item in Items.Where(i => i.Status == "Pending").ToList())
        {
            try
            {
                // If item has a profile override, apply it to encode tab before enqueueing
                EncodingProfile? profileToRestore = null;
                if (item.HasProfileOverride)
                {
                    profileToRestore = _encodeTab.CaptureCurrentSettings();
                    var profile = _services.ProfileManager.Load(item.ProfileOverride);
                    if (profile is not null)
                    {
                        _encodeTab.ApplyProfile(profile);
                        // The profile may set a different Container or AudioOnly than the Encode
                        // tab had when this item's path was first derived. Re-derive now, after
                        // the profile is applied, so the filename extension matches what the
                        // muxer will actually write.
                        var dir = Path.GetDirectoryName(item.OutputFile) ?? "";
                        var name = Path.GetFileNameWithoutExtension(item.InputFile);
                        item.OutputFile = Path.Combine(dir, $"{name}{OutputSuffix}{OutputExtension()}");
                    }
                }

                // Analyze each file — pass mediaInfo directly to avoid mutating shared EncodeTab state
                var mediaInfo = await _services.MediaAnalyzer.AnalyzeAsync(item.InputFile);
                var allJobIds = _encodeTab.EnqueueFileWithAllIds(item.InputFile, item.OutputFile, mediaInfo);
                foreach (var id in allJobIds)
                    _jobToItem[id] = item;
                item.Status = "Queued";

                // Restore original encode tab settings if we applied a per-file profile
                if (profileToRestore is not null)
                    _encodeTab.ApplyProfile(profileToRestore);
            }
            catch (Exception ex)
            {
                item.Status = $"Error: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void RefreshOutputPaths()
    {
        foreach (var item in Items)
        {
            var outputDir = UseSourceDirectory
                ? Path.GetDirectoryName(item.InputFile) ?? ""
                : OutputDirectory;
            var name = Path.GetFileNameWithoutExtension(item.InputFile);
            var ext = OutputExtension();
            item.OutputFile = Path.Combine(outputDir, $"{name}{OutputSuffix}{ext}");
        }
    }

    [RelayCommand]
    private void RefreshProfiles() => LoadProfiles();

    private void LoadProfiles()
    {
        AvailableProfiles.Clear();
        AvailableProfiles.Add("(Use Encode Tab Settings)");
        foreach (var profile in _services.ProfileManager.GetAllProfiles())
            AvailableProfiles.Add(profile.Name);
    }
}

public partial class BatchItemViewModel : ObservableObject
{
    [ObservableProperty] private string _inputFile = string.Empty;
    [ObservableProperty] private string _outputFile = string.Empty;
    [ObservableProperty] private string _status = "Pending";
    [ObservableProperty] private string _profileOverride = "(Use Encode Tab Settings)";
    [ObservableProperty] private double _progress;

    public string FileName => Path.GetFileName(InputFile);
    public bool HasProfileOverride => ProfileOverride != "(Use Encode Tab Settings)";
}
