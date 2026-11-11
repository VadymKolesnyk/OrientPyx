namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A runner's place in a relay on one day (Р-21, Р-23): the team and the leg, read straight from the day link — not
/// from the computed result — so the participant tables show and edit what is stored. The team may have no number yet
/// (an imported team numbered later) and the runner may have no leg («без етапу»). <see cref="LegCount"/> is the team
/// group's leg count (at least 1); <see cref="FilledLegs"/> how many of its legs have a runner.
/// </summary>
public sealed record RelayPlacement(Guid TeamId, int? TeamNumber, string TeamName, int? Leg, int LegCount)
{
    /// <summary>The leg number «100.2»; the bare team number when the runner has no leg; blank for a team without a
    /// number.</summary>
    public string Bib => Leg is { } leg
        ? RelayTeamNumbers.Bib(TeamNumber, leg)
        : RelayTeamNumbers.Text(TeamNumber);

    /// <summary>How many of the team's legs 1…<see cref="LegCount"/> have a runner.</summary>
    public int FilledLegs { get; init; }

    /// <summary>Some leg has nobody: the team runs «поза конкурсом» until every leg is filled (Р-26).</summary>
    public bool IsIncomplete => FilledLegs < LegCount;
}

/// <summary>
/// A change to a runner's relay place typed in the participant tables. The team is <see cref="TeamId"/> when given (a
/// leg edit on the runner's own team, which may have no number), else the team numbered <see cref="TeamNumber"/>; both
/// null takes the runner off their team. <see cref="Leg"/> null keeps the leg the runner has (when it is free on the new
/// team), else the team's first free leg — like the import (Р-25). <see cref="ReplaceOccupant"/> confirms putting the
/// runner on a leg another runner holds (that runner is left without a leg).
/// </summary>
public sealed record RelayPlacementRequest(
    Guid ParticipantId, Guid DayId, int? TeamNumber, int? Leg, bool ReplaceOccupant = false, Guid? TeamId = null);

/// <summary>Which team cell of a runner's row was typed in: «Команда» (the team's name) or «Номер команди».</summary>
public enum RelayTeamField
{
    Name,
    Number
}

/// <summary>What to do when a typed team name / number is not the runner's team and could mean two things.</summary>
public enum RelayTeamChoice
{
    /// <summary>Find out: an edit that could change the team itself comes back as
    /// <see cref="RelayPlacementStatus.ConfirmTeamChange"/> / <see cref="RelayPlacementStatus.NumberTaken"/>.</summary>
    Ask,

    /// <summary>Change the runner's team itself — rename / renumber it for all its runners.</summary>
    ChangeTeam,

    /// <summary>Move only this runner — to the team with that name / number, created when there is none.</summary>
    MoveRunner
}

/// <summary>
/// Another group picked for a runner who is on a relay team (Р-22: on a relay day their group is the team's), after the
/// page asked: <see cref="RelayTeamChoice.MoveRunner"/> takes only this runner off the team and into the group (they stay
/// on the day); <see cref="RelayTeamChoice.ChangeTeam"/> moves the whole team into it — a relay group of the day only.
/// </summary>
public sealed record RelayGroupEdit(Guid ParticipantId, Guid DayId, Guid GroupId, RelayTeamChoice Choice);

/// <summary>
/// A runner's «Команда» or «Номер команди» cell typed in the participant tables (<paramref name="Value"/> as typed;
/// blank for a cleared cell).
/// </summary>
public sealed record RelayTeamCellEdit(
    Guid ParticipantId, Guid DayId, RelayTeamField Field, string Value, RelayTeamChoice Choice = RelayTeamChoice.Ask);

/// <summary>What a <see cref="RelayPlacementRequest"/> / <see cref="RelayTeamCellEdit"/> did, or why it did nothing.</summary>
public enum RelayPlacementStatus
{
    /// <summary>The runner is on the team and leg asked for.</summary>
    Placed,

    /// <summary>The runner moved to a leg of their own team held by a teammate; the two swapped legs.</summary>
    Swapped,

    /// <summary>The runner was taken off their team (they stay on the day, in their group).</summary>
    Released,

    /// <summary>The runner's team itself was renamed / renumbered (for all its runners).</summary>
    TeamChanged,

    /// <summary>Nothing to change (already there / not on a team).</summary>
    Unchanged,

    /// <summary>Another runner holds the leg; ask, then repeat with <see cref="RelayPlacementRequest.ReplaceOccupant"/>.</summary>
    LegTaken,

    /// <summary>The team has a runner on every leg and no leg was named.</summary>
    TeamFull,

    /// <summary>The leg is outside 1…the group's leg count.</summary>
    LegOutOfRange,

    /// <summary>No such team and no relay group on the day to create it in.</summary>
    NoRelayGroup,

    /// <summary>No such team and the runner's group is not a relay one while the day has several.</summary>
    AmbiguousGroup,

    /// <summary>Several teams of the day have that name and none is in the runner's group.</summary>
    AmbiguousTeam,

    /// <summary>The typed name / number is not a team of the day: ask whether to rename / renumber the runner's team
    /// (<see cref="RelayTeamChoice.ChangeTeam"/>) or move the runner to a new one (<see cref="RelayTeamChoice.MoveRunner"/>,
    /// offered when <see cref="RelayPlacementOutcome.CanMoveRunner"/>).</summary>
    ConfirmTeamChange,

    /// <summary>Another team has the typed number: ask whether to move the runner to it
    /// (<see cref="RelayTeamChoice.MoveRunner"/>).</summary>
    NumberTaken
}

/// <summary>
/// The result of a <see cref="RelayPlacementRequest"/>: the team (number — null for a team without one — and name) and
/// leg it ended on (or was refused for), the other runner involved (the swapped teammate / the leg's holder), the
/// team's leg count, whether the team was created by this edit, the group the runner was moved to (empty when their
/// group stayed) and the runner's place now (<see cref="Place"/>, null once off a team) — so the edited row can show it
/// before the tables reload.
/// </summary>
public sealed record RelayPlacementOutcome(
    RelayPlacementStatus Status,
    int? TeamNumber = null,
    int Leg = 0,
    string OtherRunner = "",
    int LegCount = 0,
    bool TeamCreated = false,
    string MovedToGroup = "",
    RelayPlacement? Place = null,
    string TeamName = "")
{
    /// <summary>How a message names the team: «101», or its name for a team without a number.</summary>
    public string TeamLabel => RelayTeamNumbers.Label(TeamNumber, TeamName);

    /// <summary>The other team involved (<see cref="RelayPlacementStatus.NumberTaken"/> / <see cref="RelayPlacementStatus.AmbiguousTeam"/>),
    /// as a message names it.</summary>
    public string OtherTeam { get; init; } = string.Empty;

    /// <summary>The name / number typed (<see cref="RelayPlacementStatus.ConfirmTeamChange"/>); blank for a cleared cell.</summary>
    public string NewValue { get; init; } = string.Empty;

    /// <summary>How many runners the team has (a rename / renumber reaches all of them).</summary>
    public int MemberCount { get; init; }

    /// <summary><see cref="RelayPlacementStatus.ConfirmTeamChange"/>: moving only this runner is an option too.</summary>
    public bool CanMoveRunner { get; init; }

    /// <summary>True when the stored relay places changed (the tables must reload).</summary>
    public bool Changed => Status is RelayPlacementStatus.Placed or RelayPlacementStatus.Swapped
        or RelayPlacementStatus.Released or RelayPlacementStatus.TeamChanged;
}
