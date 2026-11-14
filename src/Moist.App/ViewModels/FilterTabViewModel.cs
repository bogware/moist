using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Moist.Services;
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
        LoadChain(preset.EngineId, preset.Filters.Select(pf => (pf.FilterId, pf.Parameters)).ToList());
    }

    /// <summary>
    /// Replace the current chain with the given engine + filters, keeping the model (<see cref="Chain"/>)
    /// and the UI mirror (<see cref="AppliedFilters"/>) in sync. Shared by preset-apply and
    /// profile-apply so neither silently populates only one of the two.
    /// </summary>
    public void LoadChain(string? engineId, IReadOnlyList<(string FilterId, Dictionary<string, object> Parameters)> filters)
    {
        var engine = Engines.FirstOrDefault(e => e.Id == engineId) ?? SelectedEngine;
        if (engine is null) return;

        if (SelectedEngine?.Id != engine.Id)
            SelectedEngine = engine;

        ClearFilters();

        var engineFilters = engine.GetAvailableFilters();
        foreach (var (filterId, parameters) in filters)
        {
            var filterDef = engineFilters.FirstOrDefault(f => f.Id == filterId);
            if (filterDef is null) continue;

            // Values loaded from a saved profile/preset arrive as System.Text.Json JsonElements
            // (the dict is object-typed). Coerce each to the parameter's declared CLR type so
            // downstream Convert.ToXxx (param VMs, FilterChain.Validate) and script generation
            // don't choke on a JsonElement that isn't IConvertible.
            var paramValues = new Dictionary<string, object>();
            foreach (var (key, param) in filterDef.Parameters)
            {
                if (parameters.TryGetValue(key, out var raw))
                    paramValues[key] = FilterParamCoercion.Coerce(raw, param);
                else if (param.DefaultValue is not null)
                    paramValues[key] = param.DefaultValue;
            }

            // Share the same dict instance between chain and VM for sync
            Chain.Add(filterDef, paramValues);
            // Get the actual dict from the chain (same reference since Add stores what we pass)
            var chainParams = Chain.Filters[^1].ParameterValues;
            var afvm = new AppliedFilterViewModel(filterDef, chainParams);
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

    /// <summary>Bumped on every preview request so a stale off-thread result is discarded.</summary>
    private int _previewGeneration;

    private void UpdateScriptPreview()
    {
        if (SelectedEngine is null) return;

        // GenerateScript can block: a chain containing the GPU "superres" filter triggers the
        // one-time SuperResDetector Vulkan probe (an ffmpeg spawn + WaitForExit of up to 20 s) on
        // the first, uncached call. Running that on the UI thread freezes the Filters tab, so
        // generate off-thread and marshal the result back. Snapshot the chain on the UI thread
        // first — the background task must not enumerate the live chain while the user keeps
        // editing it.
        var engine = SelectedEngine;
        var snapshot = new FilterChain();
        foreach (var f in Chain.Filters)
            snapshot.Add(f.Filter, new Dictionary<string, object>(f.ParameterValues));

        var generation = ++_previewGeneration;
        _ = Task.Run(() =>
        {
            string script;
            try { script = engine.GenerateScript(snapshot, "<input_file>"); }
            catch (Exception ex) { script = $"// preview unavailable: {ex.Message}"; }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Latest-wins: drop this result if a newer edit already superseded it.
                if (generation == _previewGeneration)
                    ScriptPreview = script;
            });
        });
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
        // Defensive: a JsonElement here (e.g. a value that bypassed LoadChain) would throw in the
        // Convert.ToXxx calls below, since JsonElement isn't IConvertible.
        value = FilterParamCoercion.Unwrap(value);
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

    // Only the editor for this parameter's own type may write its value. The Filters tab builds an
    // editor of every type for every parameter and merely hides the others — and a hidden decimal
    // editor bound to an int parameter starts at 0, gets clamped up to the parameter's Minimum by
    // its NumericUpDown/Slider, and pushes that back. Opening the Filters tab turned Deblock's
    // strength 4 into 1 that way (its minimum), silently changing every later encode.
    partial void OnIntValueChanged(int value) { if (IsInt) Value = value; }
    partial void OnDoubleValueChanged(double value) { if (IsDouble) Value = value; }
    partial void OnBoolValueChanged(bool value) { if (IsBool) Value = value; }
    partial void OnStringValueChanged(string value) { if (IsString) Value = value; }
    partial void OnSelectedOptionChanged(string? value) { if (IsChoice && value is not null) Value = value; }
}

/// <summary>
/// Coerces filter-parameter values loaded from JSON into the CLR primitives the rest of the app
/// expects. System.Text.Json deserializes object-typed dictionary entries as <see cref="JsonElement"/>,
/// which is not IConvertible — passing one to Convert.ToInt32/ToDouble/ToBoolean throws
/// "Object must implement IConvertible", which is what bombed profile-apply for filter profiles.
/// </summary>
internal static class FilterParamCoercion
{
    /// <summary>Unwrap a <see cref="JsonElement"/> to a CLR primitive; pass anything else through.</summary>
    public static object? Unwrap(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => e.ToString(),
        },
        _ => value,
    };

    /// <summary>Unwrap and convert to the parameter's declared type, falling back to its default.</summary>
    public static object Coerce(object? raw, FilterParameter def)
    {
        var v = Unwrap(raw) ?? def.DefaultValue;
        if (v is null) return def.DefaultValue ?? string.Empty;
        try
        {
            return def.Type switch
            {
                FilterParameterType.Int => Convert.ToInt32(v),
                FilterParameterType.Double => Convert.ToDouble(v),
                FilterParameterType.Bool => Convert.ToBoolean(v),
                _ => v.ToString() ?? string.Empty,
            };
        }
        catch
        {
            return def.DefaultValue ?? v;
        }
    }
}
