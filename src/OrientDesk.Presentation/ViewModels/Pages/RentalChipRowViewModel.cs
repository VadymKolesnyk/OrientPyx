using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
