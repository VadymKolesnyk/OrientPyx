using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

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
public sealed class GroupRankingContext
{
    public Guid? GroupId { get; init; }

    /// <summary>The group's name (for a plugin's ranking context).</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>The discipline's effective settings on the day.</summary>
    public SettingValues DisciplineSettings { get; init; } = SettingValues.Empty;

    /// <summary>Participant views by link id (for a plugin's ranking context).</summary>
    public IReadOnlyDictionary<Guid, ParticipantView> Participants { get; init; } = new Dictionary<Guid, ParticipantView>();

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
