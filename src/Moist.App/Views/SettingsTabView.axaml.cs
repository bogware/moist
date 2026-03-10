using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class SettingsTabView : UserControl
{
    private static readonly FilePickerFileType ExecutableType = new("Executables")
    {
        Patterns = OperatingSystem.IsWindows()
            ? ["*.exe"]
            : ["*"],
    };

    public SettingsTabView()
    {
        InitializeComponent();
    }

    private async void BrowseToolPath_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsTabViewModel vm) return;
            if (sender is not Button btn || btn.Tag is not string toolName) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Select {toolName} executable",
                AllowMultiple = false,
                FileTypeFilter = [ExecutableType, new FilePickerFileType("All Files") { Patterns = ["*"] }],
            });

            if (files.Count > 0)
            {
                var path = files[0].TryGetLocalPath();
                if (path is not null)
                    vm.SetToolPath(toolName, path);
            }
        }
        catch { /* File picker failure is non-critical */ }
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not SettingsTabViewModel vm) return;
            if (sender is not Button btn || btn.Tag is not string toolName) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Select {toolName} directory",
                AllowMultiple = false,
            });

            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (path is not null)
                    vm.SetToolPath(toolName, path);
            }
        }
        catch { /* Folder picker failure is non-critical */ }
    }
}
