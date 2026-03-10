using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class PluginManagerView : UserControl
{
    public PluginManagerView()
    {
        InitializeComponent();
    }

    private async void AddLocalSource_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not PluginManagerViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Plugin Directory",
                AllowMultiple = false,
            });

            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (path is not null)
                {
                    vm.AddLocalSourceCommand.Execute(path);
                }
            }
        }
        catch { /* Folder picker failure is non-critical */ }
    }
}
