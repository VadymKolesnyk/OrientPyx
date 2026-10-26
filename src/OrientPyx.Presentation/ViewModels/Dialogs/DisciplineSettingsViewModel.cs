using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Налаштування дисципліни…»: an auto-built form over the settings of every discipline used on a day that has
/// any (one section per discipline). Completes with the edited values per discipline, or null on cancel.
/// </summary>
public sealed partial class DisciplineSettingsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<DisciplineId, IReadOnlyDictionary<string, object?>>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DisciplineSettingsViewModel(
        ILocalizationService localization, string dayTitle,
        IEnumerable<(string Title, DisciplineSettingsData Data)> disciplines)
    {
        Localization = localization;
        DayTitle = dayTitle;
        Sections = disciplines
            .Select(d => new DisciplineSettingsSection(d.Data.DisciplineId, d.Title,
                d.Data.Definitions.Select(def => new SettingItemViewModel(localization, d.Data.PluginId, def,
                    d.Data.Values.GetValueOrDefault(def.Key))).ToList()))
            .ToList();
    }

    public ILocalizationService Localization { get; }

    public string DayTitle { get; }

    public IReadOnlyList<DisciplineSettingsSection> Sections { get; }

    public Task<IReadOnlyDictionary<DisciplineId, IReadOnlyDictionary<string, object?>>?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm() =>
        _completion.TrySetResult(Sections.ToDictionary(
            s => s.DisciplineId,
            s => (IReadOnlyDictionary<string, object?>)s.Items.ToDictionary(i => i.Key, i => i.Value)));

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One discipline's settings in the dialog.</summary>
public sealed record DisciplineSettingsSection(DisciplineId DisciplineId, string Title, IReadOnlyList<SettingItemViewModel> Items);

/// <summary>One setting row: a label and the editor for its kind (stepper, check box, text, choice).</summary>
public sealed partial class SettingItemViewModel : ObservableObject
{
    private readonly SettingDef _def;

    public SettingItemViewModel(ILocalizationService localization, string? pluginId, SettingDef def, object? value)
    {
        _def = def;
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
        _ => Text
    };

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
