using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType VideoFileType = new("Video Files")
    {
        Patterns = ["*.mkv", "*.mp4", "*.avi", "*.mov", "*.wmv", "*.flv", "*.webm",
                    "*.m4v", "*.mpg", "*.mpeg", "*.ts", "*.m2ts", "*.vob"],
    };

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;

            if (e.KeyModifiers == KeyModifiers.Control)
            {
                switch (e.Key)
                {
                    case Key.O: // Ctrl+O: Open file
                        e.Handled = true;
                        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = "Open Video File",
                            AllowMultiple = false,
                            FileTypeFilter = [VideoFileType],
                        });
                        if (files.Count > 0)
                        {
                            var path = files[0].TryGetLocalPath();
                            if (path is not null)
                            {
                                vm.SelectedTabIndex = 0; // Switch to Encode tab
                                await vm.EncodeTab.HandleDroppedFile(path);
                            }
                        }
                        break;

                    case Key.Q: // Ctrl+Q: Add to queue
                        e.Handled = true;
                        vm.EncodeTab.AddToQueueCommand.Execute(null);
                        break;

                    case Key.R: // Ctrl+R: Start queue
                        e.Handled = true;
                        if (!vm.QueueTab.IsRunning)
                            vm.QueueTab.StartQueueCommand.Execute(null);
                        break;

                    case Key.S: // Ctrl+S: Save settings
                        e.Handled = true;
                        vm.SettingsTab.SaveCommand.Execute(null);
                        break;

                    // Ctrl+1..6: Switch tabs
                    case Key.D1: e.Handled = true; vm.SelectedTabIndex = 0; break;
                    case Key.D2: e.Handled = true; vm.SelectedTabIndex = 1; break;
                    case Key.D3: e.Handled = true; vm.SelectedTabIndex = 2; break;
                    case Key.D4: e.Handled = true; vm.SelectedTabIndex = 3; break;
                    case Key.D5: e.Handled = true; vm.SelectedTabIndex = 4; break;
                    case Key.D6: e.Handled = true; vm.SelectedTabIndex = 5; break;
                }
            }

            if (e.KeyModifiers == KeyModifiers.None)
            {
                switch (e.Key)
                {
                    case Key.F1: // F1: About
                        e.Handled = true;
                        ShowAbout();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.StatusText = $"Error: {ex.Message}";
        }
    }

    private void AboutButton_Click(object? sender, RoutedEventArgs e) => ShowAbout();

    private async void ShowAbout()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
            var center = Avalonia.Layout.HorizontalAlignment.Center;
            var dialog = new Window
            {
                Title = "About moist",
                Width = 420,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Margin = new Avalonia.Thickness(32),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "moist", FontSize = 32, FontWeight = Avalonia.Media.FontWeight.Bold,
                            HorizontalAlignment = center },
                        new TextBlock { Text = "Modern Open-source Intelligent Streaming Transcoder",
                            FontSize = 11, Opacity = 0.6, HorizontalAlignment = center,
                            TextAlignment = Avalonia.Media.TextAlignment.Center },
                        new Separator { Margin = new Avalonia.Thickness(40, 10) },
                        new TextBlock { Text = $"Version {version}", FontSize = 13,
                            HorizontalAlignment = center },
                        new TextBlock { Text = ".NET 8 + Avalonia UI", FontSize = 11, Opacity = 0.4,
                            HorizontalAlignment = center },
                        new TextBlock { Text = $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
                            FontSize = 10, Opacity = 0.35, HorizontalAlignment = center },
                        new TextBlock { Text = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
                            FontSize = 10, Opacity = 0.35, HorizontalAlignment = center,
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                        new Separator { Margin = new Avalonia.Thickness(40, 10) },
                        new TextBlock { Text = "A man's gotta encode what a man's gotta encode.",
                            FontSize = 10, Opacity = 0.3, FontStyle = Avalonia.Media.FontStyle.Italic,
                            HorizontalAlignment = center },
                    }
                }
            };
            await dialog.ShowDialog(this);
        }
        catch { /* About dialog failure is non-critical */ }
    }
}
