using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>One finish-status breakdown line for the dashboard «Фінішувало» tile: the status code and how
/// many of the day's finishers carry it. Only statuses with a non-zero count are produced.</summary>
public sealed record DashboardStatusCount(FinishStatus Status, int Count);

/// <summary>
/// Snapshot shown on the dashboard («Панель»): an overview of the selected competition and the
/// current day, with live counts (participants, groups, rental chips, finish read-outs and run
/// results). Built by <see cref="Interfaces.ICompetitionEditorService.GetDashboardAsync"/> from the
/// event database; <see cref="HasSelection"/> is false when no competition is open.
/// </summary>
public sealed class DashboardInfo
{
    /// <summary>False when no competition is selected — the dashboard then shows an empty state.</summary>
    public bool HasSelection { get; init; }

    // --- Competition summary ---
    public string CompetitionName { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;

    /// <summary>Human-readable date span (single date or "start – end"); empty when no dates set.</summary>
    public string DateRange { get; init; } = string.Empty;

    public int DayCount { get; init; }

    // --- Current day ---
    public int CurrentDayNumber { get; init; }

    /// <summary>The current day's calendar date formatted dd.MM.yyyy, or empty when unset.</summary>
    public string CurrentDayDate { get; init; } = string.Empty;

    /// <summary>The current day's default discipline (вид змагань); the VM localizes its name.</summary>
    public DisciplineType CurrentDayDiscipline { get; init; }

    // --- Participants & groups ---
    /// <summary>Total participants in the competition (across all days).</summary>
    public int ParticipantTotal { get; init; }

    /// <summary>Participants running on the current day (members of the day).</summary>
    public int ParticipantsToday { get; init; }

    /// <summary>Groups attached to the current day.</summary>
    public int GroupsToday { get; init; }

    // --- Rental chips ---
    public int ChipsTotal { get; init; }
    public int ChipsHandedOut { get; init; }
    public int ChipsFree { get; init; }

    // --- Start (current day) ---
    /// <summary>Earliest assigned start time among the day's members, or null when none is drawn.</summary>
    public TimeSpan? FirstStart { get; init; }

    /// <summary>Latest assigned start time among the day's members, or null when none is drawn.</summary>
    public TimeSpan? LastStart { get; init; }

    /// <summary>Day members with a start time assigned (жеребкування done for them).</summary>
    public int StartsAssigned { get; init; }

    /// <summary>Day members still without a chip (a pre-start data check).</summary>
    public int WithoutChip { get; init; }

    /// <summary>Day members not yet assigned to a group (a pre-start data check).</summary>
    public int WithoutGroup { get; init; }

    // --- Finish read & results (current day) ---
    /// <summary>Read-out rows logged for the current day.</summary>
    public int ReadoutsToday { get; init; }

    /// <summary>Day members whose run computed to a valid (OK) finish.</summary>
    public int FinishedOk { get; init; }

    /// <summary>Day members with a result but a problem status (MP / OVT / DNF / DSQ …).</summary>
    public int FinishedWithProblem { get; init; }

    /// <summary>Total day members who finished — anyone with a non-blank status (OK plus every problem
    /// status). The big number on the «Фінішувало» tile; <see cref="FinishedByStatus"/> breaks it down.</summary>
    public int FinishedTotal { get; init; }

    /// <summary>Per-status breakdown of the day's finishers (OK, MP, OVT, DNF, DNS, DSQ …), only statuses
    /// with a non-zero count, in enum order. Sums to <see cref="FinishedTotal"/>.</summary>
    public IReadOnlyList<DashboardStatusCount> FinishedByStatus { get; init; } = [];

    /// <summary>
    /// Day members still on course: no actual (chip) start, no finish and a blank status — the same
    /// three-field test the «На дистанції» participants filter uses, so this count matches that filtered
    /// list. These are the runners results are still expected for.
    /// </summary>
    public int OnCourse { get; init; }

    /// <summary>The latest assigned start time-of-day among runners still on course, or null when none are
    /// on course (or none of them has a drawn start). The VM shows the elapsed time since this — how long the
    /// last person who is still out has been running.</summary>
    public TimeSpan? LastOnCourseStart { get; init; }
}
