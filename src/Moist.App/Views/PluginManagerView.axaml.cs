using System;
using System.Diagnostics;
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

    /// <summary>
    /// Opens a plugin's project page in the system browser so the user can download it manually.
    /// Used for catalog entries that have no direct download link. Best-effort — a failed launch
    /// is swallowed.
    /// </summary>
    private void OpenPluginPage_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control { DataContext: PluginPackageViewModel pkg }) return;

            var url = !string.IsNullOrWhiteSpace(pkg.Website) ? pkg.Website : pkg.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            // Only launch web links — never hand an arbitrary local path to the shell.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return;

            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });

            if (DataContext is PluginManagerViewModel vm)
                vm.StatusMessage =
                    $"Opened {pkg.Name} in your browser — download it, place the files in the plugins directory, then restart moist.";
        }
        catch { /* Launching a browser is best-effort */ }
    }
}
