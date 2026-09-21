using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class EncodeTabView : UserControl
{
    private static readonly FilePickerFileType VideoFileType = new("Video Files")
    {
        Patterns = ["*.mkv", "*.mp4", "*.avi", "*.mov", "*.wmv", "*.flv", "*.webm",
                    "*.m4v", "*.mpg", "*.mpeg", "*.ts", "*.m2ts", "*.vob",
                    "*.avs", "*.vpy", "*.d2v", "*.dgi"],
    };

    private static readonly FilePickerFileType AllFilesType = new("All Files")
    {
        Patterns = ["*"],
    };

    public EncodeTabView()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // Avalonia 11.3 deprecation — DataTransfer API incompatible
        if (DataContext is EncodeTabViewModel vm && e.Data.Contains(DataFormats.Files))
#pragma warning restore CS0618
        {
            vm.IsDragOver = true;
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (DataContext is EncodeTabViewModel vm)
            vm.IsDragOver = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not EncodeTabViewModel vm) return;

            vm.IsDragOver = false;

#pragma warning disable CS0618
            var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618
            if (files is null || files.Count == 0) return;

            var firstFile = files[0].TryGetLocalPath();
            if (firstFile is not null)
            {
                await vm.HandleDroppedFile(firstFile);
            }
        }
        catch (Exception ex)
        {
            if (DataContext is EncodeTabViewModel vm)
            {
                vm.HasError = true;
                vm.StatusMessage = $"Drop failed: {ex.Message}";
            }
        }
    }

    private void FullPreview_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EncodeTabViewModel vm) return;

        // Non-modal + owned, so the user can keep tweaking settings on the Encode tab and hit
        // "Generate" again — the preview re-reads the current state via BuildPreviewRequest.
        var previewVm = new PreviewViewModel(vm.Services, vm.BuildPreviewRequest, vm.MediaInfo, vm.InputFile);
        var window = new PreviewWindow(previewVm);
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    private async void BrowseInput_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not EncodeTabViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Video File",
                AllowMultiple = false,
                FileTypeFilter = [VideoFileType, AllFilesType],
            });

            if (files.Count > 0)
            {
                var path = files[0].TryGetLocalPath();
                if (path is not null)
                {
                    await vm.HandleDroppedFile(path);
                }
            }
        }
        catch (Exception ex)
        {
            if (DataContext is EncodeTabViewModel vm)
            {
                vm.HasError = true;
                vm.StatusMessage = $"Failed to open file: {ex.Message}";
            }
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not EncodeTabViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Encoded File As",
                SuggestedFileName = string.IsNullOrEmpty(vm.OutputFile)
                    ? "output"
                    : Path.GetFileName(vm.OutputFile),
                FileTypeChoices =
                [
                    new FilePickerFileType("MKV") { Patterns = ["*.mkv"] },
                    new FilePickerFileType("MP4") { Patterns = ["*.mp4"] },
                    new FilePickerFileType("WebM") { Patterns = ["*.webm"] },
                    new FilePickerFileType("AVI") { Patterns = ["*.avi"] },
                    AllFilesType,
                ],
            });

            if (file is not null)
            {
                var path = file.TryGetLocalPath();
                if (path is not null)
                    vm.OutputFile = path;
            }
        }
        catch (Exception ex)
        {
            if (DataContext is EncodeTabViewModel vm)
            {
                vm.HasError = true;
                vm.StatusMessage = $"Failed to select output: {ex.Message}";
            }
        }
    }

    private async void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not EncodeTabViewModel vm) return;

            // Simple text input dialog — use a small window
            var dialog = new Window
            {
                Title = "Save Profile",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var textBox = new TextBox
            {
                Watermark = "Profile name...",
                Margin = new Avalonia.Thickness(16, 16, 16, 8),
            };

            var saveButton = new Button
            {
                Content = "Save",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(16, 0, 16, 16),
            };

            saveButton.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(textBox.Text))
                {
                    vm.SaveProfileCommand.Execute(textBox.Text);
                    dialog.Close();
                }
            };

            dialog.Content = new StackPanel
            {
                Children = { textBox, saveButton }
            };

            var parent = TopLevel.GetTopLevel(this) as Window;
            if (parent is not null)
                await dialog.ShowDialog(parent);
        }
        catch { /* Dialog failure is non-critical */ }
    }
}
