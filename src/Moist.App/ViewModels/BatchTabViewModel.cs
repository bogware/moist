using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.App.Services;
using Moist.Core.Jobs;
using Moist.Core.Muxers;

namespace Moist.App.ViewModels;

public partial class BatchTabViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly EncodeTabViewModel _encodeTab;
    private readonly Dictionary<string, BatchItemViewModel> _jobToItem = new();

    public ObservableCollection<BatchItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _outputDirectory = string.Empty;
    [ObservableProperty] private string _outputSuffix = "_encoded";
    [ObservableProperty] private bool _useSourceDirectory = true;

    public BatchTabViewModel(AppServices services, EncodeTabViewModel encodeTab)
    {
        _services = services;
        _encodeTab = encodeTab;
        _outputDirectory = services.Settings.DefaultOutputDirectory;

        // Track job status changes for batch items
        services.JobQueue.JobStatusChanged += (_, job) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_jobToItem.TryGetValue(job.Id, out var item))
                {
                    item.Status = job.Status switch
                    {
                        JobStatus.Running => "Encoding",
                        JobStatus.Completed => "Completed",
                        JobStatus.Failed => $"Failed: {job.ErrorMessage}",
                        JobStatus.Cancelled => "Cancelled",
                        _ => item.Status
                    };
                }
            });
        };
    }

    public void HandleDroppedFiles(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            if (Items.Any(i => i.InputFile == path)) continue;

            var outputDir = UseSourceDirectory
                ? Path.GetDirectoryName(path) ?? ""
                : OutputDirectory;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = _encodeTab.Container.GetExtension();

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
                // Analyze each file — pass mediaInfo directly to avoid mutating shared EncodeTab state
                var mediaInfo = await _services.MediaAnalyzer.AnalyzeAsync(item.InputFile);
                var muxJobId = _encodeTab.EnqueueFile(item.InputFile, item.OutputFile, mediaInfo);
                _jobToItem[muxJobId] = item;
                item.Status = "Queued";
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
            var ext = _encodeTab.Container.GetExtension();
            item.OutputFile = Path.Combine(outputDir, $"{name}{OutputSuffix}{ext}");
        }
    }
}

public partial class BatchItemViewModel : ObservableObject
{
    [ObservableProperty] private string _inputFile = string.Empty;
    [ObservableProperty] private string _outputFile = string.Empty;
    [ObservableProperty] private string _status = "Pending";

    public string FileName => Path.GetFileName(InputFile);
}
