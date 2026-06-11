namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Aggregate ("Мандатка" / roster) model: one row per competition participant, with a
/// <see cref="RosterDayCell"/> for every day of the competition. Identity fields are the
/// competition-level participant values (shared across days); the per-day cells carry that day's
/// membership and group assignment.
/// </summary>
public sealed record ParticipantRosterRow(
    Guid ParticipantId,
    string Surname,
    string Name,
    string Number,
    string Rank,
    string Coach,
    DateTimeOffset? BirthDate,
    IReadOnlyList<RosterDayCell> Days);

/// <summary>
/// One participant's standing on one day in the roster view. <see cref="IsMember"/> false (and a
/// null <see cref="LinkId"/>) means the participant does not run that day — the cell renders greyed.
/// Picking a group on a non-member cell creates the link (joins the day); clearing it removes the link.
/// </summary>
public sealed record RosterDayCell(
    Guid DayId,
    int DayNumber,
    Guid? LinkId,
    bool IsMember,
    Guid? GroupId,
    string GroupName);
