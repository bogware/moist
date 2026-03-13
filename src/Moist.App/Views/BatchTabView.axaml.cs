using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class BatchTabView : UserControl
{
    private static readonly FilePickerFileType VideoFileType = new("Video Files")
    {
        Patterns = ["*.mkv", "*.mp4", "*.avi", "*.mov", "*.wmv", "*.flv", "*.webm",
                    "*.m4v", "*.mpg", "*.mpeg", "*.ts", "*.m2ts", "*.vob"],
    };

    public BatchTabView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Avalonia 11.3 deprecation — DataTransfer API incompatible
        if (e.Data.Contains(DataFormats.Files))
#pragma warning restore CS0618
            e.DragEffects = DragDropEffects.Copy;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not BatchTabViewModel vm) return;

#pragma warning disable CS0618
        var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618
        if (files is null || files.Count == 0) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Cast<string>();

        vm.HandleDroppedFiles(paths);
    }

    private async void BrowseFiles_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not BatchTabViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Video Files for Batch Encoding",
                AllowMultiple = true,
                FileTypeFilter = [VideoFileType, new FilePickerFileType("All Files") { Patterns = ["*"] }],
            });

            if (files.Count > 0)
            {
                var paths = files
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => p is not null)
                    .Cast<string>();

                vm.HandleDroppedFiles(paths);
            }
        }
        catch { /* File picker failure is non-critical */ }
    }
}
