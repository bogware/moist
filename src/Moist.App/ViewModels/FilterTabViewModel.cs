using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.App.Services;
using Moist.Filters;

namespace Moist.App.ViewModels;

public partial class FilterTabViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ObservableCollection<IFilterEngine> Engines { get; } = [];
    public ObservableCollection<FilterDefinition> AvailableFilters { get; } = [];
    public ObservableCollection<FilterDefinition> FilteredAvailableFilters { get; } = [];
    public ObservableCollection<AppliedFilterViewModel> AppliedFilters { get; } = [];
    public ObservableCollection<FilterPreset> Presets { get; } = [];
    public ObservableCollection<FilterPreset> FilteredPresets { get; } = [];
    public FilterChain Chain { get; } = new();

    [ObservableProperty] private IFilterEngine? _selectedEngine;
    [ObservableProperty] private FilterDefinition? _selectedAvailableFilter;
    [ObservableProperty] private AppliedFilterViewModel? _selectedAppliedFilter;
    [ObservableProperty] private FilterPreset? _selectedPreset;
    [ObservableProperty] private string _scriptPreview = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _selectedCategory;

    public static IReadOnlyList<string> AllCategories { get; } =
        Enum.GetNames<FilterCategory>().ToList();

    public FilterTabViewModel(AppServices services)
    {
        _services = services;

        // Detect which engines are actually available
        _ = DetectEnginesAsync();

        foreach (var engine in services.Filters.Engines)
            Engines.Add(engine);

        SelectedEngine = Engines.FirstOrDefault();
        RefreshPresets();
    }

    private async Task DetectEnginesAsync()
    {
        try { await _services.Filters.DetectAvailableEnginesAsync(); }
        catch { /* non-critical — engines default to their initial state */ }
    }

    partial void OnSelectedEngineChanged(IFilterEngine? value)
    {
        AvailableFilters.Clear();
        if (value is null) return;

        foreach (var f in value.GetAvailableFilters())
            AvailableFilters.Add(f);

        ApplyFilterSearch();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilterSearch();
    partial void OnSelectedCategoryChanged(string? value) => ApplyFilterSearch();

    private void ApplyFilterSearch()
    {
        FilteredAvailableFilters.Clear();

        foreach (var f in AvailableFilters)
        {
            if (!string.IsNullOrWhiteSpace(SearchText)
                && !f.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                && !f.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                && !f.Category.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                continue;

            if (SelectedCategory is not null
                && f.Category.ToString() != SelectedCategory)
                continue;

            FilteredAvailableFilters.Add(f);
        }
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        FilteredPresets.Clear();

        foreach (var p in _services.FilterPresets.Presets)
        {
            Presets.Add(p);
            FilteredPresets.Add(p);
        }
    }

    [RelayCommand]
    private void AddFilter()
    {
        if (SelectedAvailableFilter is null) return;

        var defaults = new Dictionary<string, object>();
        foreach (var (key, param) in SelectedAvailableFilter.Parameters)
        {
            if (param.DefaultValue is not null)
                defaults[key] = param.DefaultValue;
        }

        Chain.Add(SelectedAvailableFilter, defaults);
        var vm = new AppliedFilterViewModel(SelectedAvailableFilter, defaults);
        WireParameterChanges(vm);
        AppliedFilters.Add(vm);
        SelectedAppliedFilter = vm;
        UpdateScriptPreview();
    }

    private void WireParameterChanges(AppliedFilterViewModel filterVm)
    {
        foreach (var pvm in filterVm.ParameterViewModels)
        {
            pvm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterParameterViewModel.Value) && pvm.Value is not null)
                    NotifyParameterChanged(filterVm, pvm.Key, pvm.Value);
            };
        }
    }

    [RelayCommand]
    private void RemoveFilter(AppliedFilterViewModel? filter)
    {
        if (filter is null) return;
        var idx = AppliedFilters.IndexOf(filter);
        if (idx >= 0)
        {
            Chain.Remove(idx);
            AppliedFilters.RemoveAt(idx);
            if (SelectedAppliedFilter == filter)
                SelectedAppliedFilter = AppliedFilters.Count > 0 ? AppliedFilters[Math.Min(idx, AppliedFilters.Count - 1)] : null;
            UpdateScriptPreview();
        }
    }

    [RelayCommand]
    private void MoveFilterUp(AppliedFilterViewModel? filter)
    {
        if (filter is null) return;
        var idx = AppliedFilters.IndexOf(filter);
        if (idx > 0)
        {
            Chain.MoveUp(idx);
            AppliedFilters.Move(idx, idx - 1);
            UpdateScriptPreview();
        }
    }

    [RelayCommand]
    private void MoveFilterDown(AppliedFilterViewModel? filter)
    {
        if (filter is null) return;
        var idx = AppliedFilters.IndexOf(filter);
        if (idx >= 0 && idx < AppliedFilters.Count - 1)
        {
            Chain.MoveDown(idx);
            AppliedFilters.Move(idx, idx + 1);
            UpdateScriptPreview();
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Chain.Clear();
        AppliedFilters.Clear();
        SelectedAppliedFilter = null;
        ScriptPreview = string.Empty;
    }

    [RelayCommand]
    private void ApplyPreset(FilterPreset? preset)
    {
        if (preset is null) return;

        var engine = Engines.FirstOrDefault(e => e.Id == preset.EngineId) ?? SelectedEngine;
        if (engine is null) return;

        if (SelectedEngine?.Id != engine.Id)
            SelectedEngine = engine;

        ClearFilters();

        var engineFilters = engine.GetAvailableFilters();
        foreach (var pf in preset.Filters)
        {
            var filterDef = engineFilters.FirstOrDefault(f => f.Id == pf.FilterId);
            if (filterDef is null) continue;

            var paramValues = new Dictionary<string, object>(pf.Parameters);
            foreach (var (key, param) in filterDef.Parameters)
            {
                if (!paramValues.ContainsKey(key) && param.DefaultValue is not null)
                    paramValues[key] = param.DefaultValue;
            }

            Chain.Add(filterDef, paramValues);
            var afvm = new AppliedFilterViewModel(filterDef, paramValues);
            WireParameterChanges(afvm);
            AppliedFilters.Add(afvm);
        }

        if (AppliedFilters.Count > 0)
            SelectedAppliedFilter = AppliedFilters[0];

        UpdateScriptPreview();
    }

    [RelayCommand]
    private void SaveCurrentAsPreset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || AppliedFilters.Count == 0) return;

        var preset = new FilterPreset
        {
            Id = $"user-{Guid.NewGuid():N}",
            Name = name,
            EngineId = SelectedEngine?.Id ?? "ffmpeg",
            Category = "User",
            Filters = AppliedFilters.Select(af => new PresetFilter
            {
                FilterId = af.FilterId,
                Parameters = new Dictionary<string, object>(af.Parameters),
            }).ToList(),
        };

        _services.FilterPresets.SaveUserPreset(preset);
        RefreshPresets();
    }

    [RelayCommand]
    private void DeletePreset(FilterPreset? preset)
    {
        if (preset is null || preset.IsBuiltIn) return;
        _services.FilterPresets.DeleteUserPreset(preset.Id);
        RefreshPresets();
    }

    [RelayCommand]
    private void ClearCategoryFilter()
    {
        SelectedCategory = null;
    }

    public void NotifyParameterChanged(AppliedFilterViewModel filter, string key, object value)
    {
        filter.Parameters[key] = value;

        var idx = AppliedFilters.IndexOf(filter);
        if (idx >= 0 && idx < Chain.Filters.Count)
            Chain.Filters[idx].ParameterValues[key] = value;

        UpdateScriptPreview();
    }

    private void UpdateScriptPreview()
    {
        if (SelectedEngine is null) return;
        ScriptPreview = SelectedEngine.GenerateScript(Chain, "<input_file>");
    }
}

public partial class AppliedFilterViewModel : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _category;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _isEnabled = true;

    public string FilterId { get; }
    public Dictionary<string, object> Parameters { get; }
    public FilterDefinition FilterDefinition { get; }
    public ObservableCollection<FilterParameterViewModel> ParameterViewModels { get; } = [];

    public AppliedFilterViewModel(FilterDefinition filter, Dictionary<string, object> parameters)
    {
        FilterId = filter.Id;
        _name = filter.Name;
        _category = filter.Category.ToString();
        _description = filter.Description;
        FilterDefinition = filter;
        Parameters = parameters;

        foreach (var (key, paramDef) in filter.Parameters)
        {
            var value = parameters.TryGetValue(key, out var v) ? v : paramDef.DefaultValue;
            ParameterViewModels.Add(new FilterParameterViewModel(key, paramDef, value));
        }
    }
}

public partial class FilterParameterViewModel : ObservableObject
{
    public string Key { get; }
    public string Name { get; }
    public string Description { get; }
    public FilterParameterType Type { get; }
    public object? MinValue { get; }
    public object? MaxValue { get; }
    public string[]? Options { get; }

    [ObservableProperty] private object? _value;
    [ObservableProperty] private double _doubleValue;
    [ObservableProperty] private int _intValue;
    [ObservableProperty] private bool _boolValue;
    [ObservableProperty] private string _stringValue = string.Empty;
    [ObservableProperty] private string? _selectedOption;

    public bool IsInt => Type == FilterParameterType.Int;
    public bool IsDouble => Type == FilterParameterType.Double;
    public bool IsBool => Type == FilterParameterType.Bool;
    public bool IsString => Type == FilterParameterType.String;
    public bool IsChoice => Type == FilterParameterType.Choice;

    public double SliderMin => MinValue is not null ? Convert.ToDouble(MinValue) : 0;
    public double SliderMax => MaxValue is not null ? Convert.ToDouble(MaxValue) : 100;

    public FilterParameterViewModel(string key, FilterParameter paramDef, object? value)
    {
        Key = key;
        Name = paramDef.Name;
        Description = paramDef.Description;
        Type = paramDef.Type;
        MinValue = paramDef.MinValue;
        MaxValue = paramDef.MaxValue;
        Options = paramDef.Options;
        _value = value;

        switch (Type)
        {
            case FilterParameterType.Int:
                _intValue = value is not null ? Convert.ToInt32(value) : 0;
                break;
            case FilterParameterType.Double:
                _doubleValue = value is not null ? Convert.ToDouble(value) : 0.0;
                break;
            case FilterParameterType.Bool:
                _boolValue = value is not null && Convert.ToBoolean(value);
                break;
            case FilterParameterType.String:
                _stringValue = value?.ToString() ?? string.Empty;
                break;
            case FilterParameterType.Choice:
                _selectedOption = value?.ToString() ?? paramDef.Options?.FirstOrDefault();
                break;
        }
    }

    partial void OnIntValueChanged(int value) => Value = value;
    partial void OnDoubleValueChanged(double value) => Value = value;
    partial void OnBoolValueChanged(bool value) => Value = value;
    partial void OnStringValueChanged(string value) => Value = value;
    partial void OnSelectedOptionChanged(string? value) { if (value is not null) Value = value; }
}
