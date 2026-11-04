namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// What an all-days participant import would do, worked out before it runs so the user can confirm it row by
/// row (<c>ICompetitionEditorService.PreviewParticipantImportAsync</c>): the file rows that add a new athlete,
/// the rows that update an existing one (matched by FOU code, the way the import itself matches) with the
/// fields that would change, and the athletes in the database the file no longer lists. The user unticks rows
/// to leave out (<see cref="ParticipantImportScope.ExcludedRows"/>) and ticks missing athletes to delete.
/// </summary>
public sealed record ImportPreview(
    IReadOnlyList<ImportPreviewAdded> Added,
    IReadOnlyList<ImportPreviewUpdated> Updated,
    IReadOnlyList<ImportPreviewMissing> Missing)
{
    public static ImportPreview Empty { get; } = new([], [], []);

    public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Missing.Count == 0;
}

/// <summary>A file row that would add a new athlete.</summary>
public sealed record ImportPreviewAdded(
    int RowIndex,
    string FullName,
    string Group,
    int? BirthYear,
    string Club,
    string Region,
    IReadOnlyList<int> DayNumbers);

/// <summary>A file row that would update an existing athlete, with every field that would change. An empty
/// <see cref="Changes"/> list means the row is identical to what is stored.</summary>
public sealed record ImportPreviewUpdated(
    int RowIndex,
    Guid ParticipantId,
    string FullName,
    string Group,
    IReadOnlyList<ImportPreviewChange> Changes);

/// <summary>
/// One field an update would change. <see cref="Field"/> is a stable token the UI labels: a participant-level
/// column name (<c>FullName</c>, <c>Number</c>, <c>Team</c>, <c>Rank</c>, <c>Coach</c>, <c>BirthDate</c>,
/// <c>Region</c>, <c>Club</c>, <c>Dussh</c>, <c>Representative</c>, <c>IsFsouMember</c>, <c>Payment</c>),
/// <c>field.&lt;key&gt;</c> for an additional field, or — with <see cref="DayNumber"/> set — <c>Group</c>,
/// <c>Chip</c>, or <c>Day</c> (the athlete is newly entered on that day).
/// </summary>
public sealed record ImportPreviewChange(string Field, int? DayNumber, string Before, string After);

/// <summary>An athlete in the database the file does not list (never touched by the import unless the user
/// chooses to delete them).</summary>
public sealed record ImportPreviewMissing(
    Guid ParticipantId,
    string FullName,
    string Groups,
    int? BirthYear,
    string Club,
    string Region,
    IReadOnlyList<int> DayNumbers);
