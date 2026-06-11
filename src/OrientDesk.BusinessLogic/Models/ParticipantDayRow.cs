using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Flat read/write model for one participant on one day: joins a <c>Participant</c> (identity fields,
/// shared across days) with its <c>ParticipantDay</c> link (this day's group and chip) so the UI
/// handles a single row per competitor on the day. <see cref="GroupId"/> null means "no group yet";
/// <see cref="GroupName"/> carries the resolved name for display. <see cref="DayDefaultDiscipline"/>
/// lets the row decide whether the discipline-specific Team column is relevant.
/// </summary>
public sealed record ParticipantDayRow(
    Guid LinkId,
    Guid ParticipantId,
    int Order,
    string Surname,
    string Name,
    string Number,
    string Rank,
    string Coach,
    DateTimeOffset? BirthDate,
    Guid? GroupId,
    string GroupName,
    string Chip,
    string Team,
    DisciplineType DayDefaultDiscipline);
