using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Moist.App.ViewModels;
using Moist.Core.Tools;

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
        Opened += OnOpened;
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.RequiredToolsMissing += OnRequiredToolsMissing;
    }

    private async void OnRequiredToolsMissing(object? sender, List<ToolStatus> missingTools)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Wait a beat for the window to fully render before showing the dialog
        await Task.Delay(200);

        await ShowMissingToolsDialog(vm, missingTools);
    }

    private async Task ShowMissingToolsDialog(MainWindowViewModel vm, List<ToolStatus> missingTools)
    {
        var toolNames = string.Join(", ", missingTools.Select(t => t.Tool.Name));
        var canAutoInstall = missingTools.Any(t => t.Tool.DownloadUrls.Count > 0);

        var statusLabel = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var installButton = new Button
        {
            Content = "Download && Install",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(24, 8),
            IsVisible = canAutoInstall,
            FontWeight = FontWeight.SemiBold,
        };

        var settingsButton = new Button
        {
            Content = "Open Settings",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(24, 8),
        };

        var continueButton = new Button
        {
            Content = "Continue Anyway",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 6),
            FontSize = 11,
            Opacity = 0.6,
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Children = { installButton, settingsButton },
        };

        var dialog = new Window
        {
            Title = "moist — Prerequisites Required",
            Width = 480,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(32, 24),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Prerequisites Not Found",
                        FontSize = 22,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new Separator { Margin = new Thickness(40, 6) },
                    new TextBlock
                    {
                        Text = $"moist requires {toolNames} to encode video and audio.\n\nWithout these tools, encoding will not work.",
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        MaxWidth = 400,
                        Margin = new Thickness(0, 4, 0, 8),
                    },
                    statusLabel,
                    progressBar,
                    new Separator { Margin = new Thickness(40, 8) },
                    buttonPanel,
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { continueButton },
                    },
                }
            }
        };

        continueButton.Click += (_, _) => dialog.Close();

        settingsButton.Click += (_, _) =>
        {
            vm.SelectedTabIndex = 5; // Settings tab
            dialog.Close();
        };

        installButton.Click += async (_, _) =>
        {
            installButton.IsEnabled = false;
            settingsButton.IsEnabled = false;
            installButton.Content = "Installing...";
            progressBar.IsVisible = true;

            var allSucceeded = true;
            foreach (var missing in missingTools)
            {
                if (missing.Tool.DownloadUrls.Count == 0) continue;

                var progress = new Progress<(string Status, double Percent)>(p =>
                {
                    statusLabel.Text = p.Status;
                    if (p.Percent >= 0)
                        progressBar.Value = p.Percent;
                });

                var result = await vm.InstallToolAsync(missing.Tool, progress);
                if (result is null)
                {
                    allSucceeded = false;
                    statusLabel.Text = $"Failed to install {missing.Tool.Name}. You can configure paths manually in Settings.";
                    installButton.Content = "Retry";
                    installButton.IsEnabled = true;
                    settingsButton.IsEnabled = true;
                    progressBar.IsVisible = false;
                    return;
                }
            }

            if (allSucceeded)
            {
                statusLabel.Text = "All prerequisites installed successfully!";
                progressBar.Value = 100;
                await Task.Delay(1200);
                dialog.Close();
            }
        };

        await dialog.ShowDialog(this);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var s = vm.Services.Settings;

        Width = s.WindowWidth;
        Height = s.WindowHeight;

        if (s.WindowX is not null && s.WindowY is not null)
            Position = new Avalonia.PixelPoint((int)s.WindowX.Value, (int)s.WindowY.Value);

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Nothing in here may throw: an exception on the closing path leaves the
        // window un-closable and the process hung.
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var s = vm.Services.Settings;

            if (WindowState == WindowState.Maximized)
            {
                // Keep the previously saved restore geometry — maximized bounds aren't useful.
                s.WindowMaximized = true;
            }
            else
            {
                s.WindowMaximized = false;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
                s.WindowX = Position.X;
                s.WindowY = Position.Y;
            }
            s.Save();

            // Release the Encode tab's cancellation tokens and preview bitmap. Safe here because
            // the window is closing for good, not on a transient DataContext change.
            vm.EncodeTab.Dispose();

            // Queued encodes are recorded as they're enqueued and cleared as they finish,
            // so there is nothing to write here — whatever is still outstanding is already
            // on disk and will be offered back on the next launch.
        }
        catch
        {
            // Best effort — never block shutdown.
        }
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
                    case Key.T: // Ctrl+T: Toggle theme
                        e.Handled = true;
                        var settings = vm.Services.Settings;
                        var newTheme = settings.Theme == "moist Dark" ? "Fluent Dark" : "moist Dark";
                        settings.Theme = newTheme;
                        vm.SettingsTab.SelectedTheme = newTheme;
                        if (Avalonia.Application.Current is App app)
                            app.ApplyTheme(newTheme);
                        vm.StatusText = $"Theme: {newTheme}";
                        break;
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

                    case Key.Delete: // Del: Remove selected job from queue
                        if (vm.SelectedTabIndex == MainWindowViewModel.QueueTabIndex && vm.QueueTab.SelectedJob is not null)
                        {
                            e.Handled = true;
                            vm.QueueTab.RemoveJobCommand.Execute(vm.QueueTab.SelectedJob);
                        }
                        break;

                    case Key.Space: // Space: Toggle queue start/stop
                        if (vm.SelectedTabIndex == MainWindowViewModel.QueueTabIndex)
                        {
                            e.Handled = true;
                            if (vm.QueueTab.IsRunning)
                                vm.QueueTab.StopQueueCommand.Execute(null);
                            else
                                vm.QueueTab.StartQueueCommand.Execute(null);
                        }
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

    // === Custom title bar (native chrome removed via ExtendClientAreaChromeHints=NoChrome) ===

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only the left button drags; clicks on the About/caption Buttons are already marked
        // handled by Button, so they never reach here and never start a drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ToggleMaximize(); e.Handled = true; return; }
        BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        // Fully-qualified: an unqualified Path would bind to System.IO.Path here.
        var icon = this.FindControl<Avalonia.Controls.Shapes.Path>("MaximizeIcon");
        if (icon is not null)
            icon.Data = WindowState == WindowState.Maximized
                ? Geometry.Parse("M0,3 L7,3 L7,10 L0,10 Z M3,3 L3,0 L10,0 L10,7 L7,7")  // restore
                : Geometry.Parse("M0,0 L10,0 L10,10 L0,10 Z");                            // maximize
        var button = this.FindControl<Button>("MaximizeButton");
        if (button is not null)
            ToolTip.SetTip(button, WindowState == WindowState.Maximized ? "Restore" : "Maximize");
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
