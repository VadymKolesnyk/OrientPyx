using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>What <see cref="IDisciplineStrategy.ResolveStart"/> needs to decide where a runner's time starts.</summary>
public sealed class StartContext
{
    /// <summary>The chip's own start box from the read-out; null when none was punched.</summary>
    public DateTimeOffset? ChipStart { get; init; }

    /// <summary>The assigned start (time of day, from the draw / manual entry); null when none.</summary>
    public TimeSpan? AssignedStart { get; init; }

    /// <summary>The read-out's finish time — supplies the date for <see cref="AssignedStart"/>.</summary>
    public DateTimeOffset? FinishTime { get; init; }
}

/// <summary>What <see cref="IDisciplineStrategy.AdjustTime"/> sees for one OK result.</summary>
public sealed class AdjustContext
{
    /// <summary>The runner's participant-day link.</summary>
    public required ParticipantDay Link { get; init; }

    /// <summary>The competition-level participant (name, birth date, club…); null if it could not be resolved.</summary>
    public Participant? Participant { get; init; }

    /// <summary>The group's day settings, when the runner has a group.</summary>
    public GroupDaySettings? Settings { get; init; }

    /// <summary>The actual result time (finish − start).</summary>
    public required TimeSpan Time { get; init; }

    /// <summary>The finish judgement the time belongs to (status, detail, full list of missed controls).</summary>
    public FinishStatusResult Finish { get; init; }

    /// <summary>The competition day's date (Р-46: the day's date, else the earliest finish, else today).</summary>
    public DateOnly Date { get; init; }

    /// <summary>Discipline-specific values for this result; becomes <see cref="ParticipantDayResult.Extra"/>.</summary>
    public required IDictionary<string, object?> Extra { get; init; }
}

/// <summary>One placeable individual result handed to <see cref="IDisciplineStrategy.Rank"/>: OK, in competition.</summary>
/// <param name="Scored">True when the discipline scores points (then <paramref name="Score"/> ranks first).</param>
/// <param name="Time">The (adjusted) result time; <see cref="TimeSpan.MaxValue"/> when not positive.</param>
public sealed record RankEntry(Guid LinkId, bool Scored, int Score, TimeSpan Time);

/// <summary>
/// One team-format (rogaine) member's contribution to the team tally. A team is the members sharing the same
/// (<see cref="GroupId"/>, <see cref="TeamKey"/>) on the day. <see cref="Ok"/> false (a problem status, or — via
/// <see cref="Incomplete"/> — no read-out at all) makes the whole team unplaced; <see cref="Punched"/> is this
/// member's set of visited allowed controls (intersected across the team for the team score);
/// <see cref="Elapsed"/> feeds the team penalty (max time) and the tie-break (last finisher).
/// </summary>
public sealed record TeamMemberEntry(
    Guid LinkId, Guid? GroupId, string TeamKey, bool OutOfCompetition, bool Ok,
    IReadOnlySet<string> Punched, TimeSpan? Elapsed, GroupDaySettings? Settings, int? Bonus)
{
    /// <summary>A member with no read-out: present in the team but not finished, so the team is incomplete
    /// (unless they are поза конкурсом, in which case they don't drop the team).</summary>
    public static TeamMemberEntry Incomplete(Guid linkId, Guid? groupId, string teamKey, bool outOfCompetition, int? bonus) =>
        new(linkId, groupId, teamKey, outOfCompetition, Ok: false,
            Punched: new HashSet<string>(StringComparer.OrdinalIgnoreCase), Elapsed: null, Settings: null,
            Bonus: bonus);
}

/// <summary>
/// One group's competitors for <see cref="IDisciplineStrategy.Rank"/>. An individual discipline gets its
/// placeable results in <see cref="Entries"/>; a team discipline gets every member in <see cref="TeamMembers"/>.
/// The strategy writes places (and team scores) back into <see cref="Results"/>.
/// </summary>
public sealed class RankingContext
{
    public Guid? GroupId { get; init; }

    public GroupDaySettings? Settings { get; init; }

    /// <summary>OK, in-competition individual results (individual disciplines only).</summary>
    public IReadOnlyList<RankEntry> Entries { get; init; } = [];

    /// <summary>Every member of the group on the day (team disciplines only).</summary>
    public IReadOnlyList<TeamMemberEntry> TeamMembers { get; init; } = [];

    /// <summary>The day's per-control point values.</summary>
    public IReadOnlyDictionary<string, int> PointsByCode { get; init; } = new Dictionary<string, int>();

    /// <summary>The day's results keyed by participant-day link id, updated in place.</summary>
    public required IDictionary<Guid, ParticipantDayResult> Results { get; init; }
}
