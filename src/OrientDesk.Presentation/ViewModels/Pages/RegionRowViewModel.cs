using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the regions grid. Wraps a single <see cref="Region"/> plus a read-only count
/// of how many participants come from it. The name edits in the background (debounced per row) via
/// the page-supplied <c>requestSave</c> callback (mirrors <see cref="RentalChipRowViewModel"/>).
/// </summary>
public sealed partial class RegionRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTimeOffset _createdAt;
    private readonly Action<RegionRowViewModel> _requestSave;

    // Suppresses save requests while the constructor seeds initial values.
    private readonly bool _initialized;

    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Read-only count of participants from this region (across the whole competition). Set by the
    /// page from the region-count lookup; it is not persisted and never triggers a save.
    /// </summary>
    [ObservableProperty]
    private int _participantCount;

    public RegionRowViewModel(
        Region region,
        ILocalizationService localization,
        Action<RegionRowViewModel> requestSave)
    {
        _id = region.Id;
        _createdAt = region.CreatedAt;
        _requestSave = requestSave;
        Localization = localization;

        _name = region.Name;

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public Region ToEntity() => new()
    {
        Id = _id,
        Name = (Name ?? string.Empty).Trim(),
        CreatedAt = _createdAt
    };

    partial void OnNameChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}
