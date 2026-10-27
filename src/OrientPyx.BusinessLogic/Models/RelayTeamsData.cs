using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>A relay group's settings on the day (Р-27…Р-29, Р-35), edited on the groups page.</summary>
/// <param name="RestartTimes">Restart time of day per leg (legs 2…N); a missing leg has no restart.</param>
/// <param name="CourseMode">How each leg gets its course (Р-41).</param>
/// <param name="LegCourses"><see cref="RelayCourseMode.ByLeg"/>: course code per leg.</param>
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
    IReadOnlyDictionary<int, string>? LegCourses = null,
    string TeamCourseSet = "",
    IReadOnlyList<string>? CourseCodes = null);

/// <summary>Everything the «Команди» page shows for the current day.</summary>
public sealed record RelayTeamsData(
    IReadOnlyList<RelayGroupSettings> Groups,
    IReadOnlyList<RelayTeamRow> Teams,
    IReadOnlyList<RelayRunner> Runners);

/// <summary>One relay team with its legs and its computed result (null before anything is computed).</summary>
public sealed record RelayTeamRow(
    Guid Id,
    Guid GroupId,
    int Number,
    string Name,
    Guid? ClubId,
    Guid? RegionId,
    bool OutOfCompetition,
    FinishStatus? StatusOverride,
    IReadOnlyList<RelayLegRow> Legs,
    TeamDayResult? Result,
    string CourseSet = "");

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

/// <summary>An edit of a team's own fields (the legs are assigned separately).</summary>
public sealed record RelayTeamEdit(
    Guid Id,
    Guid GroupId,
    int Number,
    string Name,
    Guid? ClubId,
    Guid? RegionId,
    bool OutOfCompetition,
    FinishStatus? StatusOverride,
    string CourseSet = "");

/// <summary>The course of one leg number («100.2» → B) for <see cref="RelayCourseMode.ByBib"/> (Р-41); a blank
/// course clears it.</summary>
public sealed record LegCourseEntry(int TeamNumber, int Leg, string CourseKey);
