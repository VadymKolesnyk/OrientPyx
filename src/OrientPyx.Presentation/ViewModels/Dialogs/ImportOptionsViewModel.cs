using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// A reusable confirmation modal for "Import from XML…" flows. It shows a title, a short message,
/// a configurable list of yes/no <see cref="ImportOption"/>s, and OK/Cancel buttons.
///
/// The set of options is passed in by the caller, so the same modal serves different imports:
/// the control-points import shows one toggle ("replace all"), while the future course/group
/// import can show two — without changing this class. Callers <c>await</c>
/// <see cref="Completion"/> to learn whether OK was pressed and how the toggles were set.
/// </summary>
public sealed partial class ImportOptionsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ImportOptionsResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ImportOptionsViewModel(
        ILocalizationService localization,
        string titleKey,
        string messageKey,
        IReadOnlyList<ImportOption> options,
        ImportScopeChoice? scope = null)
    {
        Localization = localization;
        TitleKey = titleKey;
        MessageKey = messageKey;
        Options = new ObservableCollection<ImportOption>(options);
        Scope = scope;

        // Title/message use the Localization indexer in XAML; options carry dynamic keys, so they
        // resolve their own captions and we re-raise them here on a language switch.
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            foreach (var option in Options)
                option.Refresh(Localization);
            Scope?.Refresh(Localization);
        };

        foreach (var option in Options)
            option.Refresh(Localization);
        Scope?.Refresh(Localization);
    }

    public ILocalizationService Localization { get; }

    /// <summary>
    /// Optional day-scope + link-field chooser shown above the toggles (participant imports only).
    /// Null for the plain confirm/error uses of this modal, which then hide the whole section.
    /// </summary>
    public ImportScopeChoice? Scope { get; }

    /// <summary>Whether to render the scope section (bound in XAML).</summary>
    public bool HasScope => Scope is not null;

    public string TitleKey { get; }

    public string MessageKey { get; }

    public string Title => Localization.Get(TitleKey);

    public string Message => Localization.Get(MessageKey);

    /// <summary>The toggles shown in the modal, in order. Bound to a checkbox list.</summary>
    public ObservableCollection<ImportOption> Options { get; }

    /// <summary>Completes when the user confirms (result) or cancels/closes (null).</summary>
    public Task<ImportOptionsResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        var values = Options.ToDictionary(o => o.Key, o => o.IsChecked);
        _completion.TrySetResult(new ImportOptionsResult(values, Scope?.ToScope()));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>
/// The "import onto this day only vs all days" chooser plus, when day-only is picked, the field used
/// to link imported rows to participants already imported from other days. Shown as a two-radio group
/// with a dropdown that is enabled only while the current-day radio is selected.
/// </summary>
public sealed partial class ImportScopeChoice : ObservableObject
{
    /// <param name="currentDayNumber">The active session day number, shown in the radio caption.</param>
    public ImportScopeChoice(int currentDayNumber)
    {
        CurrentDayNumber = currentDayNumber;
        LinkFields =
        [
            new LinkFieldOption(ParticipantLinkField.FsouCode, "ParticipantsImport.Scope.LinkFsou"),
            new LinkFieldOption(ParticipantLinkField.FullName, "ParticipantsImport.Scope.LinkName")
        ];
        _selectedLinkField = LinkFields[0];

        // Which global (participant-level) fields a current-day-only import may overwrite on an athlete that
        // already exists from another day. Off by default so a day import doesn't rewrite shared details;
        // only «Оплата» is pre-ticked, since that is the one field routinely re-entered per day.
        UpdateFields =
        [
            new UpdateFieldOption(ParticipantUpdateFields.FullName, "CsvImport.Field.FullName"),
            new UpdateFieldOption(ParticipantUpdateFields.Number, "CsvImport.Field.Number"),
            new UpdateFieldOption(ParticipantUpdateFields.BirthDate, "CsvImport.Field.BirthDate"),
            new UpdateFieldOption(ParticipantUpdateFields.Team, "CsvImport.Field.Team"),
            new UpdateFieldOption(ParticipantUpdateFields.Region, "CsvImport.Field.Region"),
            new UpdateFieldOption(ParticipantUpdateFields.Club, "CsvImport.Field.Club"),
            new UpdateFieldOption(ParticipantUpdateFields.Dussh, "CsvImport.Field.Dussh"),
            new UpdateFieldOption(ParticipantUpdateFields.Rank, "CsvImport.Field.Rank"),
            new UpdateFieldOption(ParticipantUpdateFields.Coach, "CsvImport.Field.Coach"),
            new UpdateFieldOption(ParticipantUpdateFields.Representative, "CsvImport.Field.Representative"),
            new UpdateFieldOption(ParticipantUpdateFields.FsouCode, "CsvImport.Field.FsouCode"),
            new UpdateFieldOption(ParticipantUpdateFields.IsFsouMember, "CsvImport.Field.IsFsouMember"),
            new UpdateFieldOption(ParticipantUpdateFields.Payment, "CsvImport.Field.Payment", isChecked: true)
        ];
    }

    public int CurrentDayNumber { get; }

    public IReadOnlyList<LinkFieldOption> LinkFields { get; }

    /// <summary>
    /// The «update this global column on existing records» checklist, shown only while current-day-only is
    /// picked (the same reason the link field is). Only the ticked fields overwrite a matched athlete.
    /// </summary>
    public IReadOnlyList<UpdateFieldOption> UpdateFields { get; }

    /// <summary>True = import onto the current day only; false = onto all days per the file (default).</summary>
    [ObservableProperty]
    private bool _currentDayOnly;

    [ObservableProperty]
    private LinkFieldOption _selectedLinkField;

    // Localized captions, refreshed on language change by the owner.
    [ObservableProperty]
    private string _allDaysLabel = string.Empty;

    [ObservableProperty]
    private string _currentDayLabel = string.Empty;

    [ObservableProperty]
    private string _linkPrompt = string.Empty;

    [ObservableProperty]
    private string _updatePrompt = string.Empty;

    public void Refresh(ILocalizationService localization)
    {
        AllDaysLabel = localization.Get("ParticipantsImport.Scope.AllDays");
        CurrentDayLabel = string.Format(localization.Get("ParticipantsImport.Scope.CurrentDay"), CurrentDayNumber);
        LinkPrompt = localization.Get("ParticipantsImport.Scope.LinkPrompt");
        UpdatePrompt = localization.Get("ParticipantsImport.Scope.UpdatePrompt");
        foreach (var option in LinkFields)
            option.Refresh(localization);
        foreach (var option in UpdateFields)
            option.Refresh(localization);
    }

    /// <summary>OR-combines the ticked update-field flags (only meaningful in current-day-only mode).</summary>
    private ParticipantUpdateFields SelectedUpdateFields()
    {
        var flags = ParticipantUpdateFields.None;
        foreach (var option in UpdateFields)
            if (option.IsChecked)
                flags |= option.Field;
        return flags;
    }

    /// <summary>Builds the layer-neutral scope the import consumes.</summary>
    public ParticipantImportScope ToScope() =>
        CurrentDayOnly
            ? ParticipantImportScope.CurrentDay(CurrentDayNumber, SelectedLinkField.Field, SelectedUpdateFields())
            : ParticipantImportScope.AllDays;
}

/// <summary>One toggle in the «update this global column on existing records» checklist.</summary>
public sealed partial class UpdateFieldOption : ObservableObject
{
    public UpdateFieldOption(ParticipantUpdateFields field, string labelKey, bool isChecked = false)
    {
        Field = field;
        LabelKey = labelKey;
        _isChecked = isChecked;
    }

    public ParticipantUpdateFields Field { get; }
    public string LabelKey { get; }

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string _label = string.Empty;

    public void Refresh(ILocalizationService localization) => Label = localization.Get(LabelKey);
}

/// <summary>One entry in the link-field dropdown.</summary>
public sealed partial class LinkFieldOption : ObservableObject
{
    public LinkFieldOption(ParticipantLinkField field, string labelKey)
    {
        Field = field;
        LabelKey = labelKey;
    }

    public ParticipantLinkField Field { get; }
    public string LabelKey { get; }

    [ObservableProperty]
    private string _label = string.Empty;

    public void Refresh(ILocalizationService localization) => Label = localization.Get(LabelKey);
}

/// <summary>One labelled toggle inside an <see cref="ImportOptionsViewModel"/>.</summary>
public sealed partial class ImportOption : ObservableObject
{
    /// <param name="key">Stable identifier used to read the value back from the result.</param>
    /// <param name="labelKey">Localization key for the checkbox caption.</param>
    /// <param name="isChecked">Initial state.</param>
    public ImportOption(string key, string labelKey, bool isChecked)
    {
        Key = key;
        LabelKey = labelKey;
        _isChecked = isChecked;
    }

    public string Key { get; }

    public string LabelKey { get; }

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Localized caption; refreshed by the owner on language change.</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>Re-resolves <see cref="Label"/> from the current language.</summary>
    public void Refresh(ILocalizationService localization) => Label = localization.Get(LabelKey);
}

/// <summary>The confirmed toggle values, keyed by each option's <see cref="ImportOption.Key"/>.</summary>
public sealed class ImportOptionsResult
{
    private readonly IReadOnlyDictionary<string, bool> _values;

    public ImportOptionsResult(IReadOnlyDictionary<string, bool> values, ParticipantImportScope? scope = null)
    {
        _values = values;
        Scope = scope;
    }

    /// <summary>The chosen import scope, when the modal showed a scope chooser; otherwise null.</summary>
    public ParticipantImportScope? Scope { get; }

    /// <summary>Reads a toggle by key; returns <paramref name="fallback"/> when it is absent.</summary>
    public bool Get(string key, bool fallback = false) =>
        _values.TryGetValue(key, out var value) ? value : fallback;
}
