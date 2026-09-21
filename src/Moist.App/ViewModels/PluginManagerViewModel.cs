using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Services;
using Moist.Filters;

namespace Moist.App.ViewModels;

public partial class PluginManagerViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ObservableCollection<PluginSourceViewModel> Sources { get; } = [];
    public ObservableCollection<PluginPackageViewModel> InstalledPlugins { get; } = [];
    public ObservableCollection<PluginPackageViewModel> OnlineCatalog { get; } = [];
    public ObservableCollection<PluginPackageViewModel> FilteredCatalog { get; } = [];

    [ObservableProperty] private PluginSourceViewModel? _selectedSource;
    [ObservableProperty] private string _catalogSearchText = string.Empty;
    [ObservableProperty] private string? _catalogEngineFilter;
    [ObservableProperty] private string? _catalogCategoryFilter;
    [ObservableProperty] private bool _isLoadingCatalog;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public static IReadOnlyList<string> EngineFilters { get; } = ["All", "ffmpeg", "vapoursynth", "avisynth"];
    public static IReadOnlyList<string> CategoryFilters { get; } = ["All", "Denoise", "Deinterlace", "Resize", "Sharpen", "FrameRate", "Custom"];

    public PluginManagerViewModel(AppServices services)
    {
        _services = services;
        RefreshSources();
        RefreshInstalledPlugins();
    }

    private void RefreshSources()
    {
        Sources.Clear();
        foreach (var source in _services.PluginSources.Sources)
        {
            Sources.Add(new PluginSourceViewModel(source));
        }
    }

    private void RefreshInstalledPlugins()
    {
        InstalledPlugins.Clear();
        foreach (var plugin in _services.PluginSources.InstalledPlugins)
        {
            InstalledPlugins.Add(new PluginPackageViewModel(plugin));
        }
    }

    [RelayCommand]
    private void AddLocalSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var source = new PluginSource
        {
            Id = $"local-{Guid.NewGuid():N}",
            Name = Path.GetFileName(path),
            Type = PluginSourceType.LocalDirectory,
            Location = path,
            Description = $"Local plugin directory: {path}",
        };

        _services.PluginSources.AddSource(source);
        RefreshSources();
        StatusMessage = $"Added local source: {path}";
    }

    [RelayCommand]
    private void RemoveSource(PluginSourceViewModel? source)
    {
        if (source is null) return;
        _services.PluginSources.RemoveSource(source.Id);
        RefreshSources();
        StatusMessage = $"Removed source: {source.Name}";
    }

    [RelayCommand]
    private void ToggleSource(PluginSourceViewModel? source)
    {
        if (source is null) return;
        source.IsEnabled = !source.IsEnabled;

        var original = _services.PluginSources.Sources.FirstOrDefault(s => s.Id == source.Id);
        if (original is not null)
        {
            original.IsEnabled = source.IsEnabled;
            _services.PluginSources.UpdateSource(original);
        }
    }

    [RelayCommand]
    private async Task LoadOnlineCatalog()
    {
        IsLoadingCatalog = true;
        StatusMessage = "Loading online catalog...";

        try
        {
            var catalog = await _services.PluginSources.GetOnlineCatalogAsync();
            OnlineCatalog.Clear();

            foreach (var pkg in catalog)
            {
                // Mark as installed if already present
                pkg.IsInstalled = _services.PluginSources.InstalledPlugins
                    .Any(p => p.Id == pkg.Id);

                OnlineCatalog.Add(new PluginPackageViewModel(pkg));
            }

            ApplyCatalogFilter();
            StatusMessage = $"Loaded {catalog.Count} plugins from catalog";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load catalog: {ex.Message}";
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    partial void OnCatalogSearchTextChanged(string value) => ApplyCatalogFilter();
    partial void OnCatalogEngineFilterChanged(string? value) => ApplyCatalogFilter();
    partial void OnCatalogCategoryFilterChanged(string? value) => ApplyCatalogFilter();

    private void ApplyCatalogFilter()
    {
        FilteredCatalog.Clear();

        foreach (var pkg in OnlineCatalog)
        {
            if (!string.IsNullOrWhiteSpace(CatalogSearchText)
                && !pkg.Name.Contains(CatalogSearchText, StringComparison.OrdinalIgnoreCase)
                && !pkg.Description.Contains(CatalogSearchText, StringComparison.OrdinalIgnoreCase)
                && !pkg.Author.Contains(CatalogSearchText, StringComparison.OrdinalIgnoreCase))
                continue;

            if (CatalogEngineFilter is not null && CatalogEngineFilter != "All"
                && pkg.Engine != CatalogEngineFilter)
                continue;

            if (CatalogCategoryFilter is not null && CatalogCategoryFilter != "All"
                && pkg.Category != CatalogCategoryFilter)
                continue;

            FilteredCatalog.Add(pkg);
        }
    }

    [RelayCommand]
    private async Task InstallPlugin(PluginPackageViewModel? pkg)
    {
        if (pkg is null || pkg.IsInstalled) return;

        if (string.IsNullOrWhiteSpace(pkg.DownloadUrl))
        {
            // No direct archive to fetch — the user installs manually from the plugin's page
            // (the "Open page" button in the view handles launching the browser).
            StatusMessage = string.IsNullOrWhiteSpace(pkg.Website)
                ? $"{pkg.Name} has no download link — install it manually into the plugins directory, then restart moist."
                : $"{pkg.Name} must be installed manually — open its page, download it, drop the files in the plugins directory, then restart moist.";
            return;
        }

        IsInstalling = true;
        try
        {
            var progress = new Progress<(string Status, double Percent)>(p => StatusMessage = p.Status);
            StatusMessage = $"Downloading {pkg.Name}...";

            var package = new PluginPackage
            {
                Id = pkg.Id,
                Name = pkg.Name,
                Version = pkg.Version,
                Description = pkg.Description,
                Author = pkg.Author,
                Category = pkg.Category,
                Engine = pkg.Engine,
                DownloadUrl = pkg.DownloadUrl,
                Website = pkg.Website,
            };

            var installedPath = await _services.PluginSources.DownloadPluginAsync(package, progress);
            if (installedPath is null)
            {
                // DownloadPluginAsync already reported the specific failure through progress.
                if (!StatusMessage.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                    && !StatusMessage.StartsWith("Refusing", StringComparison.OrdinalIgnoreCase))
                    StatusMessage = $"Failed to install {pkg.Name}. Try installing it manually from its page.";
                return;
            }

            package.IsInstalled = true;
            package.InstalledPath = installedPath;
            _services.PluginSources.AddInstalledPlugin(package);
            pkg.IsInstalled = true;
            RefreshInstalledPlugins();
            StatusMessage = $"Installed {pkg.Name} v{pkg.Version} — restart moist to load it.";
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void UninstallPlugin(PluginPackageViewModel? pkg)
    {
        if (pkg is null) return;
        _services.PluginSources.RemoveInstalledPlugin(pkg.Id);
        pkg.IsInstalled = false;
        RefreshInstalledPlugins();
        StatusMessage = $"Uninstalled: {pkg.Name}";
    }
}

public partial class PluginSourceViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _location;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _typeLabel;

    public string StatusLabel => IsEnabled ? "Enabled" : "Disabled";

    public PluginSourceViewModel(PluginSource source)
    {
        Id = source.Id;
        _name = source.Name;
        _location = source.Location;
        _description = source.Description;
        _isEnabled = source.IsEnabled;
        _typeLabel = source.Type == PluginSourceType.LocalDirectory ? "Local" : "Online";
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusLabel));
    }
}

public partial class PluginPackageViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _version;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _author;
    [ObservableProperty] private string _category;
    [ObservableProperty] private string _engine;
    [ObservableProperty] private bool _isInstalled;
    public string DownloadUrl { get; }

    /// <summary>Project/documentation page, opened in the browser for a manual install.</summary>
    public string Website { get; }

    /// <summary>True when the plugin can be downloaded directly (an Install button is shown).</summary>
    public bool HasDownloadUrl => !string.IsNullOrWhiteSpace(DownloadUrl);

    /// <summary>True when there is a page to open for a manual install.</summary>
    public bool HasWebsite => !string.IsNullOrWhiteSpace(Website);

    public PluginPackageViewModel(PluginPackage package)
    {
        Id = package.Id;
        DownloadUrl = package.DownloadUrl;
        Website = package.Website;
        _name = package.Name;
        _version = package.Version;
        _description = package.Description;
        _author = package.Author;
        _category = package.Category;
        _engine = package.Engine;
        _isInstalled = package.IsInstalled;
    }
}
