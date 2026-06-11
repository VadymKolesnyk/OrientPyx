using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One row in the roster ("Мандатка") grid: a single competition participant with a
/// <see cref="RosterDayCellViewModel"/> per day. Identity fields are competition-level and editable
/// here (affecting every day); the per-day cells carry membership and group. Identity edits invoke
/// the page-supplied <c>requestSave</c> callback (debounced); membership/group edits are handled by
/// the cells via their own callback.
/// </summary>
public sealed partial class ParticipantRosterRowViewModel : ObservableObject
{
    private readonly Guid _participantId;
    private readonly Action<ParticipantRosterRowViewModel> _requestSave;
    private readonly bool _initialized;

    [ObservableProperty]
    private string _surname;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _number;

    [ObservableProperty]
    private string _rank;

    [ObservableProperty]
    private string _coach;

    [ObservableProperty]
    private DateTimeOffset? _birthDate;

    public ParticipantRosterRowViewModel(
        ParticipantRosterRow row,
        IReadOnlyList<RosterDayCellViewModel> dayCells,
        ILocalizationService localization,
        Action<ParticipantRosterRowViewModel> requestSave)
    {
        _participantId = row.ParticipantId;
        _requestSave = requestSave;
        Localization = localization;

        Days = new ObservableCollection<RosterDayCellViewModel>(dayCells);

        _surname = row.Surname;
        _name = row.Name;
        _number = row.Number;
        _rank = row.Rank;
        _coach = row.Coach;
        _birthDate = row.BirthDate;

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid ParticipantId => _participantId;

    /// <summary>Per-day cells, in day order. Bound by the runtime per-day columns in the view.</summary>
    public ObservableCollection<RosterDayCellViewModel> Days { get; }

    partial void OnSurnameChanged(string value) => QueueSave();
    partial void OnNameChanged(string value) => QueueSave();
    partial void OnNumberChanged(string value) => QueueSave();
    partial void OnRankChanged(string value) => QueueSave();
    partial void OnCoachChanged(string value) => QueueSave();
    partial void OnBirthDateChanged(DateTimeOffset? value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}
