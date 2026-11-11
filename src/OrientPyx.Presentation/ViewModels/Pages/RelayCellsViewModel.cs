using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One runner's relay cells on one day (Р-21…Р-23), shared by a day-grid row and a roster day cell: «Команда» — the
/// team's name, typed like a rogaine team (<see cref="TeamName"/>); «Номер команди» (<see cref="TeamText"/>, the team's
/// own number — changing it is asked by the page); «Етап» (<see cref="LegText"/>); and the read-only leg number «100.2»
/// (<see cref="BibText"/>). An edit shows at once and is handed to the page, which saves it (or asks, or refuses) and
/// then reloads — a refused edit goes back with <see cref="Revert"/>. The values come from the stored link
/// (<see cref="RelayPlacement"/>), not the computed result.
/// </summary>
public sealed class RelayCellsViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Action<RelayCellsViewModel, RelayPlacementRequest> _requestPlace;
    private readonly Action<RelayCellsViewModel, RelayTeamField, string> _requestTeamEdit;
    private RelayPlacement? _stored;
    private RelayPlacement? _shown;

    public RelayCellsViewModel(
        Guid participantId,
        Guid dayId,
        RelayPlacement? stored,
        ILocalizationService localization,
        Action<RelayCellsViewModel, RelayPlacementRequest> requestPlace,
        Action<RelayCellsViewModel, RelayTeamField, string> requestTeamEdit)
    {
        ParticipantId = participantId;
        DayId = dayId;
        _stored = stored;
        _shown = stored;
        _localization = localization;
        _requestPlace = requestPlace;
        _requestTeamEdit = requestTeamEdit;
    }

    public Guid ParticipantId { get; }

    public Guid DayId { get; }

    /// <summary>The place as last read from the store (null = not on a team).</summary>
    public RelayPlacement? Stored => _stored;

    /// <summary>The leg number «100.2» (read-only: it follows the team's number and the leg); the bare team number for a
    /// runner without a leg; blank for a team without a number.</summary>
    public string BibText => _shown?.Bib ?? string.Empty;

    /// <summary>
    /// «Команда» — the team's name. Like a rogaine team: typing a team's name puts the runner on it, a new name makes a
    /// new team, an empty cell takes the runner off the team.
    /// </summary>
    public string TeamName
    {
        get => _shown?.TeamName ?? string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == TeamName)
            {
                OnPropertyChanged();
                return;
            }
            Show(text.Length == 0 ? null
                : _shown is { } shown ? shown with { TeamName = text }
                : new RelayPlacement(Guid.Empty, null, text, null, 0));
            _requestTeamEdit(this, RelayTeamField.Name, text);
        }
    }

    /// <summary>The greyed «№101» an unnamed team's «Команда» cell shows; blank otherwise.</summary>
    public string TeamNamePlaceholder => _shown is { TeamName.Length: 0, TeamNumber: { } number }
        ? string.Format(_localization.Get("Participants.Relay.UnnamedTeam"), number)
        : string.Empty;

    /// <summary>«Номер команди» — the team's own number (blank for a team that has none yet). Changing it on a runner
    /// already on a team is asked by the page (renumber the team, or move this runner); a runner on no team joins the
    /// team with that number.</summary>
    public string TeamText
    {
        get => RelayTeamNumbers.Text(_shown?.TeamNumber);
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == TeamText)
                return;
            int? number = null;
            if (text.Length > 0)
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                {
                    OnPropertyChanged();   // not a number — the cell shows the stored value again
                    return;
                }
                number = parsed;
            }
            Show(_shown is { } shown ? shown with { TeamNumber = number }
                : number is null ? null
                : new RelayPlacement(Guid.Empty, number, string.Empty, null, 0));
            _requestTeamEdit(this, RelayTeamField.Number, text);
        }
    }

    /// <summary>«Етап» — the leg within the runner's team; a leg a teammate runs swaps the two. Editable only on a team.</summary>
    public string LegText
    {
        get => _shown?.Leg?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == LegText)
                return;
            if (_shown is not { } shown || shown.TeamId == Guid.Empty
                || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var leg) || leg <= 0)
            {
                OnPropertyChanged();   // no team, or not a leg — the cell shows the stored value again
                return;
            }
            Show(shown with { Leg = leg });
            // By the team's id: it may have no number yet.
            _requestPlace(this, new RelayPlacementRequest(ParticipantId, DayId, shown.TeamNumber, leg, TeamId: shown.TeamId));
        }
    }

    /// <summary>True while the runner is on a team (the leg cell is editable then).</summary>
    public bool HasTeam => _shown is not null;

    /// <summary>Some leg of the runner's team has nobody: the team runs «поза конкурсом» until every leg is filled — the
    /// «Етап» cell is tinted amber.</summary>
    public bool IsIncomplete => _shown is { TeamId: var id, IsIncomplete: true } && id != Guid.Empty;

    /// <summary>Why the «Етап» cell is amber («зайнято 2 з 3 етапів…»); null when the team is complete.</summary>
    public string? IncompleteTooltip => IsIncomplete
        ? string.Format(_localization.Get("Participants.Relay.Incomplete"), _shown!.FilledLegs, _shown.LegCount)
        : null;

    /// <summary>Sorts «100.2» before «100.10» and before team 101; a team without a number after the numbered ones,
    /// a runner on no team last.</summary>
    public long BibSort => _shown is { } p ? RelayTeamNumbers.SortKey(p.TeamNumber) * 1000L + (p.Leg ?? 0) : long.MaxValue;

    public int TeamSort => _shown is { } p ? RelayTeamNumbers.SortKey(p.TeamNumber) : int.MaxValue;

    public int LegSort => _shown?.Leg ?? int.MaxValue;

    /// <summary>«Команда 100 «Азимут», етап 2 з 3» for the cells' hover (a team without a number by its name, a runner
    /// without a leg «без етапу»); null when not on a team.</summary>
    public string? Tooltip
    {
        get
        {
            if (_shown is not { } p || p.TeamId == Guid.Empty)
                return null;
            var team = p.TeamNumber is not { } number
                ? string.Format(_localization.Get("Participants.Relay.Tooltip.UnnumberedTeam"), p.TeamName)
                : p.TeamName.Length > 0
                    ? string.Format(_localization.Get("Participants.Relay.Tooltip.NamedTeam"), number, p.TeamName)
                    : string.Format(_localization.Get("Participants.Relay.Tooltip.Team"), number);
            return team + ", " + (p.Leg is { } leg
                ? string.Format(_localization.Get("Participants.Relay.Tooltip.Leg"), leg, p.LegCount)
                : _localization.Get("Participants.Relay.Tooltip.NoLeg"));
        }
    }

    /// <summary>Takes a freshly read place (a reload or the saved edit): the cells show what is stored; only a change
    /// repaints.</summary>
    public void Apply(RelayPlacement? stored)
    {
        _stored = stored;
        if (!Equals(_shown, stored))
            Show(stored);
    }

    /// <summary>A refused edit: the cells show the stored place again.</summary>
    public void Revert() => Show(_stored);

    // Raises every cell even when the place is the same: an editor may still hold refused text.
    private void Show(RelayPlacement? place)
    {
        _shown = place;
        OnPropertyChanged(nameof(BibText));
        OnPropertyChanged(nameof(TeamName));
        OnPropertyChanged(nameof(TeamNamePlaceholder));
        OnPropertyChanged(nameof(TeamText));
        OnPropertyChanged(nameof(LegText));
        OnPropertyChanged(nameof(HasTeam));
        OnPropertyChanged(nameof(IsIncomplete));
        OnPropertyChanged(nameof(IncompleteTooltip));
        OnPropertyChanged(nameof(BibSort));
        OnPropertyChanged(nameof(TeamSort));
        OnPropertyChanged(nameof(LegSort));
        OnPropertyChanged(nameof(Tooltip));
    }
}
