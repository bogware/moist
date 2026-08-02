using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System.Linq;
using Moist.App.ViewModels;
using Moist.App.Views;
using Moist.Core.Media;
using Moist.Core.Settings;

namespace Moist.App;

public partial class App : Application
{
    /// <summary>
    /// The moist Dark skin stylesheet, loaded from AXAML.
    /// When null or removed from Styles, the app falls back to plain Fluent Dark.
    /// </summary>
    private Styles? _moistDarkSkin;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Stash reference to the moist skin (last style added in App.axaml)
        _moistDarkSkin = Styles.OfType<Styles>().LastOrDefault()
                         ?? Styles.LastOrDefault() as Styles;

        // Apply saved theme preference
        ApplyTheme(MoistSettings.Load().Theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                // Each step is independent: a failure in one must not skip the others
                // or leave the process hanging on exit.
                try
                {
                    if (desktop.MainWindow?.DataContext is MainWindowViewModel vm)
                    {
                        vm.Services.Save();
                        vm.Services.JobQueue.Stop();
                    }
                }
                catch { /* best effort */ }

                try { VideoPreviewService.CleanupPreviewFiles(); }
                catch { /* best effort */ }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Switch between "moist Dark" (custom skin) and "Fluent Dark" (stock Avalonia).
    /// </summary>
    public void ApplyTheme(string themeName)
    {
        if (_moistDarkSkin is null) return;

        if (themeName == "Fluent Dark")
        {
            // Remove the moist overlay — plain Fluent remains
            if (Styles.Contains(_moistDarkSkin))
                Styles.Remove(_moistDarkSkin);
        }
        else
        {
            // Ensure the moist overlay is present
            if (!Styles.Contains(_moistDarkSkin))
                Styles.Add(_moistDarkSkin);
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
