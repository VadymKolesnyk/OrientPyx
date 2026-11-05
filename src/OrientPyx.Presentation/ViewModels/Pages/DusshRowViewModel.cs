using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the ДЮСШ grid. Wraps a single <see cref="Dussh"/> plus a read-only count of
/// how many participants attend it. The name edits in the background (debounced per row) via the
/// page-supplied <c>requestSave</c> callback (mirrors <see cref="RegionRowViewModel"/>).
/// </summary>
public sealed partial class DusshRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private DateTimeOffset _createdAt;
    private readonly Action<DusshRowViewModel> _requestSave;

    private bool _initialized;

    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Read-only count of participants attending this school (across the whole competition). Set by
    /// the page from the count lookup; it is not persisted and never triggers a save.
    /// </summary>
    [ObservableProperty]
    private int _participantCount;

    public DusshRowViewModel(
        Dussh dussh,
        ILocalizationService localization,
        Action<DusshRowViewModel> requestSave)
    {
        _id = dussh.Id;
        _createdAt = dussh.CreatedAt;
        _requestSave = requestSave;
        Localization = localization;

        _name = dussh.Name;

        _initialized = true;
    }

    /// <summary>True while <see cref="Apply"/> copies fresh values onto the row; the page skips its save callbacks then.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Copies a freshly-read entity (and its participant count) onto this row without triggering a save. Only the
    /// cells whose value changed raise a property change, so a live refresh touches nothing else on screen.
    /// </summary>
    public void Apply(Dussh dussh, int participantCount)
    {
        IsApplying = true;
        _initialized = false;
        try
        {
            _createdAt = dussh.CreatedAt;
            Name = dussh.Name;
            ParticipantCount = participantCount;
        }
        finally
        {
            _initialized = true;
            IsApplying = false;
        }
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public Dussh ToEntity() => new()
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
