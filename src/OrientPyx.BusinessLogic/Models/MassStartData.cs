namespace OrientPyx.BusinessLogic.Models;

/// <summary>«Масовий старт»: every group on one day with what the mass-start page shows for it.</summary>
public sealed record MassStartData(IReadOnlyList<MassStartGroup> Groups);

/// <summary>One group on the day for the mass-start page.</summary>
/// <param name="GroupId">The competition-level group id.</param>
/// <param name="Name">The group's display name (e.g. "Ч21").</param>
/// <param name="MemberCount">Runners in the group on the day; for a relay group — its teams.</param>
/// <param name="IsRelay">The group's discipline runs legs (<c>CompetitorModel.TeamWithLegs</c>).</param>
/// <param name="LegCount">Relay: legs per team (at least 1); 1 for any other group.</param>
/// <param name="CurrentStart">The start the group's runners have now when it is one time for all of them (a relay
/// group: the leg-1 start — the runner's own, else the group's mass start); null when nobody has a start or the
/// starts differ (<paramref name="MixedStarts"/>).</param>
/// <param name="MixedStarts">The runners have different start times (e.g. after an interval draw).</param>
/// <param name="MassStartTime">Relay: the group's mass start of leg 1 (Р-27); null for any other group.</param>
/// <param name="RestartTimes">Relay: the group's restart time per leg (legs 2…N, Р-29); empty otherwise.</param>
public sealed record MassStartGroup(
    Guid GroupId,
    string Name,
    int MemberCount,
    bool IsRelay,
    int LegCount,
    TimeSpan? CurrentStart,
    bool MixedStarts,
    TimeSpan? MassStartTime,
    IReadOnlyDictionary<int, TimeSpan> RestartTimes);

/// <summary>
/// One group's mass start to apply: every runner of the group starts at <see cref="Start"/>. In a relay group that
/// is the start of leg 1 (written onto the leg-1 runners and kept as the group's mass start), and
/// <see cref="RestartTimes"/> replace the group's restarts — a leg left out has none.
/// </summary>
public sealed record MassStartAssignment(Guid GroupId, TimeSpan Start, IReadOnlyDictionary<int, TimeSpan>? RestartTimes = null);

/// <summary>What a mass-start apply wrote.</summary>
/// <param name="Starts">Runners given the start time.</param>
/// <param name="RelayGroups">Relay groups whose mass start and restarts were saved.</param>
public sealed record MassStartResult(int Starts, int RelayGroups);
