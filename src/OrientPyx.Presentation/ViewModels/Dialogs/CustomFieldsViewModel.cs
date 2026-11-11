using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// The «Додаткові поля» modal: the competition's additional participant fields in a table — name, type, choice
/// options (comma-separated), origin — with add and delete (Р-13…Р-15). Edits are saved as they are made; deleting asks
/// for confirmation inline (it removes every participant's value too), and a plugin's field cannot be deleted while
/// the plugin's discipline is in use. <see cref="Changed"/> tells the caller to reload its tables.
/// </summary>
public sealed partial class CustomFieldsViewModel : ObservableObject
{
    private readonly ICompetitionEditorService _editor;
    private readonly IBusyService _busy;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Edits are written one after another (a later keystroke never lands before an earlier one).
    private Task _saveChain = Task.CompletedTask;

    public CustomFieldsViewModel(ILocalizationService localization, ICompetitionEditorService editor, IBusyService busy)
    {
        Localization = localization;
        _editor = editor;
        _busy = busy;
        KindOptions = Enum.GetValues<CustomFieldKind>().Select(k => new FieldKindOption(k, localization)).ToList();
        Localization.PropertyChanged += (_, _) =>
        {
            foreach (var option in KindOptions)
                option.Refresh();
            foreach (var row in Rows)
                row.RefreshTexts();
            OnPropertyChanged(nameof(DeletePrompt));
        };
    }

    public ILocalizationService Localization { get; }

    public IReadOnlyList<FieldKindOption> KindOptions { get; }

    public ObservableCollection<CustomFieldRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private CustomFieldRowViewModel? _selectedRow;

    /// <summary>True once anything was added, edited or deleted.</summary>
    public bool Changed { get; private set; }

    /// <summary>The row awaiting delete confirmation (shown as an inline prompt); null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingDelete), nameof(DeletePrompt))]
    private CustomFieldRowViewModel? _pendingDelete;

    public bool HasPendingDelete => PendingDelete is not null;

    public string DeletePrompt => PendingDelete is null
        ? string.Empty
        : string.Format(Localization.Get("CustomFields.DeleteConfirm"), PendingDelete.Label);

    /// <summary>A warning under the table (e.g. a plugin field that cannot be deleted); empty when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = string.Empty;

    public bool HasWarning => Warning.Length > 0;

    public Task Completion => _completion.Task;

    public async Task LoadAsync()
    {
        var fields = await _busy.RunAsync(() => _editor.GetCustomFieldsAsync());
        var locked = new HashSet<string>();
        foreach (var f in fields.Where(f => f.PluginId is not null))
            if (await _busy.RunAsync(() => _editor.IsCustomFieldLockedAsync(f.Key)))
                locked.Add(f.Key);

        Rows.Clear();
        foreach (var field in fields)
            Rows.Add(NewRow(field, locked.Contains(field.Key)));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        Warning = string.Empty;
        var label = UniqueLabel(Localization.Get("CustomFields.NewField"));
        var field = await _busy.RunAsync(() => _editor.AddCustomFieldAsync(label, CustomFieldKind.Text, []));
        if (field is null)
            return;
        Changed = true;
        var row = NewRow(field, locked: false);
        Rows.Add(row);
        SelectedRow = row;
    }

    /// <summary>Asks to delete a row (the table's delete button / Delete key); confirmed inline.</summary>
    public void RequestDelete(object? row)
    {
        Warning = string.Empty;
        if (row is not CustomFieldRowViewModel field)
            return;
        if (field.IsLocked)
        {
            Warning = Localization.Get("CustomFields.PluginLocked");
            return;
        }
        PendingDelete = field;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } row)
            return;
        PendingDelete = null;
        var deleted = await _busy.RunAsync(() => _editor.DeleteCustomFieldAsync(row.Key));
        if (!deleted)
        {
            Warning = Localization.Get("CustomFields.PluginLocked");
            return;
        }
        Changed = true;
        Rows.Remove(row);
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task CloseAsync()
    {
        // Let edits still being written land before the page reloads.
        try
        {
            await _saveChain;
        }
        catch
        {
            // a failed save was already ignored by the row; closing must still work
        }
        _completion.TrySetResult();
    }

    private CustomFieldRowViewModel NewRow(CustomField field, bool locked) =>
        new(field, locked, KindOptions, Localization, OnRowEdited);

    private void OnRowEdited(CustomFieldRowViewModel row)
    {
        Changed = true;
        var field = row.ToField();
        _saveChain = _saveChain
            .ContinueWith(_ => _editor.UpdateCustomFieldAsync(field), TaskScheduler.Default)
            .Unwrap();
    }

    private string UniqueLabel(string baseLabel)
    {
        var label = baseLabel;
        for (var i = 2; Rows.Any(r => string.Equals(r.Label, label, StringComparison.CurrentCultureIgnoreCase)); i++)
            label = $"{baseLabel} {i}";
        return label;
    }
}

/// <summary>One field in the «Додаткові поля» table. Every edit is reported to the dialog, which saves it.</summary>
public sealed partial class CustomFieldRowViewModel : ObservableObject
{
    private readonly CustomField _field;
    private readonly ILocalizationService _localization;
    private readonly Action<CustomFieldRowViewModel> _edited;
    private readonly bool _initialized;

    public CustomFieldRowViewModel(
        CustomField field,
        bool locked,
        IReadOnlyList<FieldKindOption> kindOptions,
        ILocalizationService localization,
        Action<CustomFieldRowViewModel> edited)
    {
        _field = field;
        _localization = localization;
        _edited = edited;
        IsLocked = locked;
        KindOptions = kindOptions;
        _label = field.Label;
        _selectedKind = kindOptions.First(k => k.Kind == field.Kind);
        _optionsText = ChoiceOptionsText.Format(field.Options);
        _initialized = true;
    }

    public string Key => _field.Key;

    /// <summary>True for a plugin's field whose discipline is in use (cannot be deleted, Р-15).</summary>
    public bool IsLocked { get; }

    public IReadOnlyList<FieldKindOption> KindOptions { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice))]
    private FieldKindOption _selectedKind;

    /// <summary>The choice options as text, comma-separated: <c>Ч, Ж</c>.</summary>
    [ObservableProperty]
    private string _optionsText;

    public bool IsChoice => SelectedKind.Kind == CustomFieldKind.Choice;

    /// <summary>Who created the field: the user, or the plugin (by id).</summary>
    public string SourceText => _field.PluginId is { } plugin
        ? string.Format(_localization.Get("CustomFields.Source.Plugin"), plugin)
        : _localization.Get("CustomFields.Source.User");

    public void RefreshTexts() => OnPropertyChanged(nameof(SourceText));

    partial void OnLabelChanged(string value)
    {
        if (_initialized && value.Trim().Length > 0)
            _edited(this);
    }

    partial void OnSelectedKindChanged(FieldKindOption value)
    {
        if (_initialized)
            _edited(this);
    }

    partial void OnOptionsTextChanged(string value)
    {
        if (_initialized)
            _edited(this);
    }

    public CustomField ToField() => _field with
    {
        Label = Label.Trim().Length > 0 ? Label.Trim() : _field.Label,
        Kind = SelectedKind.Kind,
        Options = SelectedKind.Kind == CustomFieldKind.Choice ? ChoiceOptionsText.Parse(OptionsText) : []
    };
}

/// <summary>A field type in the type dropdown, with its localized name.</summary>
public sealed partial class FieldKindOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public FieldKindOption(CustomFieldKind kind, ILocalizationService localization)
    {
        Kind = kind;
        _localization = localization;
    }

    public CustomFieldKind Kind { get; }

    public string Label => _localization.Get("CustomFields.Kind." + Kind);

    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>Reads and writes choice options as a comma-separated list: <c>Ч, Ж</c> (a ';' or a new line also
/// separates; a repeat — compared via <see cref="TextKey"/> — is dropped).</summary>
internal static class ChoiceOptionsText
{
    public static string Format(IReadOnlyList<ChoiceOption> options) =>
        string.Join(", ", options.Select(o => o.Value));

    public static IReadOnlyList<ChoiceOption> Parse(string? text)
    {
        var options = new List<ChoiceOption>();
        foreach (var part in (text ?? string.Empty).Split([',', ';', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!options.Any(o => TextKey.Same(o.Value, part)))
                options.Add(new ChoiceOption(part));
        }
        return options;
    }
}
