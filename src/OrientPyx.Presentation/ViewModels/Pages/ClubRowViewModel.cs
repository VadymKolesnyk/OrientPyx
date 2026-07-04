using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the clubs grid. Wraps a single <see cref="Club"/> plus a read-only count of
/// how many participants belong to it. Mirrors <see cref="RegionRowViewModel"/>.
/// </summary>
public sealed partial class ClubRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTimeOffset _createdAt;
    private readonly Action<ClubRowViewModel> _requestSave;

    private readonly bool _initialized;

    [ObservableProperty]
    private string _name;

    /// <summary>Read-only count of participants in this club (across the whole competition).</summary>
    [ObservableProperty]
    private int _participantCount;

    public ClubRowViewModel(
        Club club,
        ILocalizationService localization,
        Action<ClubRowViewModel> requestSave)
    {
        _id = club.Id;
        _createdAt = club.CreatedAt;
        _requestSave = requestSave;
        Localization = localization;

        _name = club.Name;

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public Club ToEntity() => new()
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
