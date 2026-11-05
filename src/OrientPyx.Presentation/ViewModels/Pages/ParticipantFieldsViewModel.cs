using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A participant row's additional fields (Р-13), one <see cref="ParticipantFieldCellViewModel"/> per field of the
/// competition, reachable by key so a table column binds <c>Fields[gender].Text</c>. Shared by the day grid and the
/// roster rows. An edit calls the page-supplied save callback (the page debounces and persists it).
/// </summary>
public sealed class ParticipantFieldsViewModel
{
    private readonly Dictionary<string, ParticipantFieldCellViewModel> _cells;
    private readonly IReadOnlyList<CustomField> _fields;

    /// <summary>A row with no fields (before the page attaches the real set).</summary>
    public static ParticipantFieldsViewModel Empty { get; } = new(Guid.Empty, [], new Dictionary<string, string>(), (_, _) => { });

    public ParticipantFieldsViewModel(
        Guid participantId,
        IReadOnlyList<CustomField> fields,
        IReadOnlyDictionary<string, string> values,
        Action<Guid, ParticipantFieldCellViewModel> requestSave)
    {
        _fields = fields;
        _cells = fields.ToDictionary(
            f => f.Key,
            f => new ParticipantFieldCellViewModel(
                f, values.TryGetValue(f.Key, out var v) ? v : string.Empty, cell => requestSave(participantId, cell)));
    }

    /// <summary>True when the cells were built for exactly this field set (the page keeps the list while it is unchanged).</summary>
    public bool BuiltFor(IReadOnlyList<CustomField> fields) => ReferenceEquals(_fields, fields);

    /// <summary>Copies fresh stored values onto the cells without triggering a save; only a changed cell raises.</summary>
    public void Apply(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, cell) in _cells)
            cell.ApplySaved(values.TryGetValue(key, out var v) ? v : string.Empty);
    }

    /// <summary>The cell of a field; a key the competition doesn't have reads as an inert empty cell.</summary>
    public ParticipantFieldCellViewModel this[string key] =>
        _cells.TryGetValue(key, out var cell) ? cell : ParticipantFieldCellViewModel.Missing;

    /// <summary>The current value of every field, by key (blank values included as empty).</summary>
    public IReadOnlyDictionary<string, string> Values =>
        _cells.ToDictionary(c => c.Key, c => c.Value.Value);
}

/// <summary>
/// One participant's value of one additional field, in every shape a cell needs: <see cref="Text"/> for a text or
/// number cell, <see cref="Checked"/> for a flag, <see cref="Options"/>/<see cref="Selected"/> for a choice.
/// <see cref="IsInvalid"/> tints a value outside the choice list / a non-number red (Р-18).
/// </summary>
public sealed partial class ParticipantFieldCellViewModel : ObservableObject
{
    internal static readonly ParticipantFieldCellViewModel Missing =
        new(new CustomField(string.Empty, string.Empty, CustomFieldKind.Text, [], CustomField.UserSource, 0), string.Empty, null);

    private readonly CustomField _field;
    private readonly Action<ParticipantFieldCellViewModel>? _requestSave;
    private bool _syncing;

    public ParticipantFieldCellViewModel(CustomField field, string value, Action<ParticipantFieldCellViewModel>? requestSave)
    {
        _field = field;
        _requestSave = requestSave;
        _value = value ?? string.Empty;
        Options = BuildOptions(_value);
        _selected = Options.First(o => o.Value == _value);
    }

    public string Key => _field.Key;

    /// <summary>The stored value (normalised on save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(Checked), nameof(IsInvalid))]
    private string _value;

    /// <summary>The value as edited in a text cell.</summary>
    public string Text
    {
        get => Value;
        set => Value = value ?? string.Empty;
    }

    /// <summary>The value as a flag (Bool fields): "1" ⇔ checked.</summary>
    public bool Checked
    {
        get => Value == "1";
        set => Value = value ? "1" : string.Empty;
    }

    public bool IsInvalid => _field.IsInvalid(Value);

    /// <summary>The choice options: blank first, then the field's options, plus (red) the stored value when it is
    /// outside the list so the dropdown still shows it (Р-18).</summary>
    [ObservableProperty]
    private IReadOnlyList<FieldChoiceOption> _options;

    [ObservableProperty]
    private FieldChoiceOption _selected;

    partial void OnValueChanged(string value)
    {
        if (_syncing)
            return;
        SyncSelected(value);
        _requestSave?.Invoke(this);
    }

    partial void OnSelectedChanged(FieldChoiceOption value)
    {
        if (_syncing || value is null)
            return;
        Value = value.Value;
    }

    /// <summary>Applies the value the store kept (normalised) without triggering another save.</summary>
    public void ApplySaved(string stored)
    {
        if (stored == Value)
            return;
        _syncing = true;
        try
        {
            Value = stored;
        }
        finally
        {
            _syncing = false;
        }
        SyncSelected(stored);
    }

    // Points the dropdown at the value, adding a one-off (red) option when it is outside the list.
    private void SyncSelected(string value)
    {
        _syncing = true;
        try
        {
            if (Options.FirstOrDefault(o => o.Value == value) is not { } match)
            {
                Options = BuildOptions(value);
                match = Options.First(o => o.Value == value);
            }
            Selected = match;
        }
        finally
        {
            _syncing = false;
        }
    }

    private List<FieldChoiceOption> BuildOptions(string current)
    {
        var options = new List<FieldChoiceOption> { new(string.Empty, false) };
        options.AddRange(_field.Options.Select(o => new FieldChoiceOption(o.Value, false)));
        if (current.Length > 0 && options.All(o => o.Value != current))
            options.Add(new FieldChoiceOption(current, true));
        return options;
    }
}

/// <summary>One entry of a choice field's dropdown; <see cref="IsUnknown"/> marks a stored value outside the list.</summary>
public sealed record FieldChoiceOption(string Value, bool IsUnknown)
{
    public string Label => Value;
}
