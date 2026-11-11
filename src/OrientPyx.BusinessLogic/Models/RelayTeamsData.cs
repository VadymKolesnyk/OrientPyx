using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>A relay group's settings on the day (Р-27…Р-29, Р-35), edited on the groups page.</summary>
/// <param name="RestartTimes">Restart time of day per leg (legs 2…N); a missing leg has no restart.</param>
/// <param name="CourseMode">How each leg gets its course (Р-41).</param>
/// <param name="TeamCourseSet"><see cref="RelayCourseMode.TeamSet"/>: the group's default set («A B»).</param>
/// <param name="CourseCodes">The group's named courses on the day (read only — edited with the variants).</param>
public sealed record RelayGroupSettings(
    Guid SettingsId,
    Guid GroupId,
    string GroupName,
    int LegCount,
    TimeSpan? MassStartTime,
    IReadOnlyDictionary<int, TimeSpan> RestartTimes,
    RelayRestartRanking RestartRanking,
    RelayCourseMode CourseMode = RelayCourseMode.Group,
    string TeamCourseSet = "",
    IReadOnlyList<string>? CourseCodes = null);

/// <summary>Everything the «Команди» page shows for the current day.</summary>
public sealed record RelayTeamsData(
    IReadOnlyList<RelayGroupSettings> Groups,
    IReadOnlyList<RelayTeamRow> Teams,
    IReadOnlyList<RelayRunner> Runners);

/// <summary>One relay team with its legs and its computed result (null before anything is computed).
/// <see cref="Number"/> is null until the team is given one; <see cref="Unplaced"/> lists the team's runners without a
/// leg («без етапу» — more runners than legs in an import), each to be put on a leg or taken off the team.
/// <see cref="AssignedCourseSet"/>: the courses assigned to the team's leg numbers (OCAD), in leg order — its «набір на
/// команду» while <see cref="CourseSet"/> is blank.</summary>
public sealed record RelayTeamRow(
    Guid Id,
    Guid GroupId,
    int? Number,
    string Name,
    Guid? ClubId,
    Guid? RegionId,
    bool OutOfCompetition,
    FinishStatus? StatusOverride,
    IReadOnlyList<RelayLegRow> Legs,
    TeamDayResult? Result,
    string CourseSet = "")
{
    public IReadOnlyList<RelayUnplacedRunner> Unplaced { get; init; } = [];

    public string AssignedCourseSet { get; init; } = string.Empty;
}

/// <summary>A team's runner without a leg («без етапу»).</summary>
public sealed record RelayUnplacedRunner(Guid LinkId, Guid ParticipantId, string FullName, string Chip);

/// <summary>One leg of a team: the runner on it (none when <see cref="LinkId"/> is null) and the leg's result.</summary>
public sealed record RelayLegRow(
    int Leg,
    Guid? LinkId,
    Guid? ParticipantId,
    string FullName,
    string Chip,
    RelayLegResult? Result,
    string BibCourse = "");

/// <summary>A participant who can run a leg: anyone of the competition; <see cref="LinkId"/> is their link on the
/// current day (null when they don't run it yet — assigning adds them to the day).</summary>
public sealed record RelayRunner(
    Guid ParticipantId,
    Guid? LinkId,
    string FullName,
    string Number,
    int? BirthYear,
    string Club,
    Guid? TeamId,
    int? TeamNumber,
    int? Leg);

/// <summary>An edit of a team's own fields (the legs are assigned separately); a null <see cref="Number"/> leaves
/// the team without a number.</summary>
public sealed record RelayTeamEdit(
    Guid Id,
    Guid GroupId,
    int? Number,
    string Name,
    Guid? ClubId,
    Guid? RegionId,
    bool OutOfCompetition,
    FinishStatus? StatusOverride,
    string CourseSet = "");

/// <summary>«Присвоєння номерів» on «Команди»: a team's new number; null takes the number away.</summary>
public sealed record RelayTeamNumberChange(Guid TeamId, int? Number);

/// <summary>The course of one leg number («100.2» → B) for <see cref="RelayCourseMode.ByBib"/> (Р-41); a blank
/// course clears it.</summary>
public sealed record LegCourseEntry(int TeamNumber, int Leg, string CourseKey);
