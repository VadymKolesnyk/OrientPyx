using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>One discipline's settings on a plugin's settings page.</summary>
public sealed record DisciplineSettingsSection(DisciplineId DisciplineId, string Title, string Note, IReadOnlyList<SettingItemViewModel> Items)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>One setting row: a label and the editor for its kind (stepper, check box, text, choice).</summary>
public sealed partial class SettingItemViewModel : ObservableObject
{
    private readonly SettingDef _def;
    private readonly ILocalizationService _localization;
    private readonly string? _pluginId;
    private readonly Func<string?, SettingDef, TableValue, Task<TableValue?>>? _editTable;

    public SettingItemViewModel(
        ILocalizationService localization, string? pluginId, SettingDef def, object? value,
        Func<string?, SettingDef, TableValue, Task<TableValue?>>? editTable = null)
    {
        _def = def;
        _localization = localization;
        _pluginId = pluginId;
        _editTable = editTable;
        Label = localization.Get(pluginId is null ? def.Label : PluginStrings.Key(pluginId, def.Label));
        ScopeNote = def.Scope == SettingScope.Competition ? localization.Get("Dialog.DisciplineSettings.CompetitionScope") : string.Empty;
        Choices = def.Choices;

        switch (def.Kind)
        {
            case SettingKind.Int:
                _number = Convert.ToDecimal(value ?? def.DefaultValue ?? 0, CultureInfo.InvariantCulture);
                break;
            case SettingKind.Decimal:
                _text = Convert.ToDecimal(value ?? def.DefaultValue ?? 0m, CultureInfo.InvariantCulture).ToString(CultureInfo.CurrentCulture);
                break;
            case SettingKind.Bool:
                _flag = value is true || (value is null && def.DefaultValue is true);
                break;
            case SettingKind.Table:
                _table = value as TableValue ?? def.DefaultValue as TableValue ?? TableValue.Empty;
                break;
            default:
                _text = value?.ToString() ?? def.DefaultValue?.ToString() ?? string.Empty;
                break;
        }
    }

    public string Key => _def.Key;

    public string Label { get; }

    /// <summary>«для всього змагання» for a competition-scoped setting; empty otherwise.</summary>
    public string ScopeNote { get; }

    public bool HasScopeNote => ScopeNote.Length > 0;

    public bool IsInt => _def.Kind == SettingKind.Int;

    public bool IsDecimalOrText => _def.Kind is SettingKind.Decimal or SettingKind.Text;

    public bool IsBool => _def.Kind == SettingKind.Bool;

    public bool IsChoice => _def.Kind == SettingKind.Choice;

    public bool IsTable => _def.Kind == SettingKind.Table;

    // A table setting's current rows (edited in its own sheet, see EditTable).
    private TableValue _table = TableValue.Empty;

    public string EditTableLabel => _localization.Get("Dialog.DisciplineSettings.EditTable");

    /// <summary>«13 рядків» next to a table setting's edit button.</summary>
    public string TableSummary => string.Format(_localization.Get("Dialog.DisciplineSettings.TableRows"), _table.Rows.Count);

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    private decimal _number;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _flag;

    /// <summary>The edited value, typed by the setting's kind (the service coerces and clamps it again).</summary>
    public object? Value => _def.Kind switch
    {
        SettingKind.Int => (int)Number,
        SettingKind.Decimal => DisciplineSettings.Coerce(Text, _def),
        SettingKind.Bool => Flag,
        SettingKind.Table => _table,
        _ => Text
    };

    [RelayCommand]
    private async Task EditTableAsync()
    {
        if (_editTable is null || await _editTable(_pluginId, _def, _table) is not { } edited)
            return;
        _table = edited;
        OnPropertyChanged(nameof(TableSummary));
    }

    [RelayCommand]
    private void Increment()
    {
        if (_def.Max is not { } max || Number < max)
            Number++;
    }

    [RelayCommand]
    private void Decrement()
    {
        if (_def.Min is not { } min || Number > min)
            Number--;
    }
}
