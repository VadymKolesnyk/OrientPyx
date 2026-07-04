namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Single-row table in the app database remembering the last opened competition and day,
/// used only to auto-restore on startup. Runtime selection is held in-memory, not here.
/// </summary>
public class LastSessionRow
{
    /// <summary>Fixed primary key — there is only ever one last-session row.</summary>
    public int Id { get; set; } = 1;

    /// <summary>Identifier (folder name) of the last opened competition, if any.</summary>
    public string? LastEventIdentifier { get; set; }

    /// <summary>Day number of the last opened day, if any.</summary>
    public int? LastEventDayNumber { get; set; }
}
