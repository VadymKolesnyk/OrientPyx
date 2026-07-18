using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the rental-chips grid. Wraps a single <see cref="RentalChip"/>. Edits do not
/// save directly — each change invokes the page-supplied <c>requestSave</c> callback, which
/// debounces and persists in the background (mirrors <see cref="ControlPointRowViewModel"/>).
/// </summary>
public sealed partial class RentalChipRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTimeOffset _createdAt;
    private readonly Action<RentalChipRowViewModel> _requestSave;

    // Suppresses save requests while the constructor seeds initial values.
    private readonly bool _initialized;

    [ObservableProperty]
    private string _number;

    [ObservableProperty]
    private string _note;

    /// <summary>
    /// Read-only display of who currently holds this chip: the comma-separated full names of every
    /// participant assigned this chip on any day. Empty when nobody holds it. Set by the page from the
    /// chip-holder lookup; it is not persisted on the chip and never triggers a save.
    /// </summary>
    [ObservableProperty]
    private string _assignedTo = string.Empty;

    /// <summary>
    /// True when this row's number is a duplicate of another chip in the same competition (numbers must
    /// be unique). Set by the page after every number edit; drives the red cell tint so the user sees the
    /// collision — a duplicate is never persisted (the save reverts it). Never triggers a save itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DuplicateTooltip))]
    private bool _isDuplicate;

    /// <summary>Hover text for the red duplicate tint; empty (no tooltip) unless the number is a duplicate.</summary>
    public string DuplicateTooltip => IsDuplicate ? Localization.Get("Chips.Duplicate.Tooltip") : string.Empty;

    public RentalChipRowViewModel(
        RentalChip chip,
        ILocalizationService localization,
        Action<RentalChipRowViewModel> requestSave)
    {
        _id = chip.Id;
        _createdAt = chip.CreatedAt;
        _requestSave = requestSave;
        Localization = localization;

        _number = chip.Number;
        _note = chip.Note;

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public RentalChip ToEntity() => new()
    {
        Id = _id,
        Number = (Number ?? string.Empty).Trim(),
        Note = (Note ?? string.Empty).Trim(),
        CreatedAt = _createdAt
    };

    partial void OnNumberChanged(string value) => QueueSave();
    partial void OnNoteChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}
