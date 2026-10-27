namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One relay team's computed result on a day (Р-31…Р-35). The legs are in leg order (1…<c>LegCount</c>); a leg
/// nobody runs has a <see cref="RelayLegResult"/> with no link. Each member's <see cref="ParticipantDayResult"/>
/// carries the team (<see cref="ParticipantDayResult.Team"/>) and its own leg (<see cref="ParticipantDayResult.Leg"/>).
/// </summary>
/// <param name="Status">The effective status: the judge's override, else OK when every leg is OK (and the team is
/// inside the time limit), else the worst leg status (DSQ &gt; MP &gt; OVT &gt; DNF &gt; DNS &gt; «очікує»).</param>
/// <param name="Time">The team time Σ adjusted leg times (Р-32); null until every assigned leg has a time.</param>
/// <param name="ActualTime">Finish of the last leg − start of leg 1; the time limit applies to it (Р-31).</param>
public sealed record TeamDayResult(
    Guid TeamId,
    int Number,
    string Name,
    Guid GroupId,
    FinishStatus Status,
    FinishStatus Computed,
    FinishStatus? Override,
    TimeSpan? Time,
    TimeSpan? ActualTime,
    IReadOnlyList<RelayLegResult> Legs)
{
    /// <summary>The team's place in the group; null when not placed.</summary>
    public int? Place { get; init; }

    /// <summary>Not placed «поза конкурсом»: the team flag, an incomplete team (Р-26) or an unranked restart (Р-35).</summary>
    public bool OutOfCompetition { get; init; }

    /// <summary>Fewer runners assigned than the group's leg count (Р-26).</summary>
    public bool Incomplete { get; init; }

    /// <summary>«Набір на команду»: the team's course set does not match its legs, so it cannot be closed (Р-41,
    /// Р-33) — the team is never OK.</summary>
    public bool CourseSetOpen { get; init; }

    /// <summary>Some leg started from a restart (Р-29) — «з перезапуском».</summary>
    public bool Restarted { get; init; }

    /// <summary>Why a leg's discipline rule withheld the team from ranking (Р-48); null when ranked normally.</summary>
    public string? UnrankedReason { get; init; }

    /// <summary>«Очки» of the team's place, shared by every member (Р-37).</summary>
    public decimal? Points { get; init; }

    /// <summary>Discipline values of the team row (a plugin's ranking may set them).</summary>
    public IReadOnlyDictionary<string, object?> Extra { get; init; } = new Dictionary<string, object?>();

    public string Bib(int leg) => RelayBib.Format(Number, leg);
}

/// <summary>One leg of a relay team (Р-27…Р-32).</summary>
/// <param name="Leg">1-based leg number.</param>
/// <param name="LinkId">The runner's participant-day link; null when the leg is not assigned.</param>
/// <param name="Start">Where the leg's time starts: leg 1 — start punch / assigned / mass start; leg N — the previous
/// leg's finish, or the restart (whichever is earlier).</param>
/// <param name="Status">The leg's effective status (judge's override first); <see cref="FinishStatus.None"/> for a
/// pending / unread leg.</param>
/// <param name="LegTime">finish − start of an OK leg.</param>
/// <param name="AdjustedTime">The leg time after the discipline's adjustment (Р-6, rounded when changed).</param>
/// <param name="Cumulative">Σ adjusted leg times up to and including this leg (Р-32); null after a leg without time.</param>
/// <param name="Restarted">The leg started from the group's restart (Р-29).</param>
/// <param name="Pending">«очікує»: the previous leg has no finish and there is no restart (Р-30).</param>
public sealed record RelayLegResult(
    int Leg,
    Guid? LinkId,
    Guid? ParticipantId,
    DateTimeOffset? Start,
    DateTimeOffset? Finish,
    FinishStatus Status,
    TimeSpan? LegTime,
    TimeSpan? AdjustedTime,
    TimeSpan? Cumulative,
    bool Restarted,
    bool Pending)
{
    /// <summary>The leg's named course (Р-44, «Етап 2 — дистанція B»); null when the group runs one course for all
    /// legs, or — in a «набір на команду» — while the leg is not read yet («не визначено», Р-43).</summary>
    public string? CourseCode { get; init; }

    /// <summary>The group names its leg courses but none could be assigned to this leg (Р-41).</summary>
    public bool CourseUnassigned { get; init; }
}
