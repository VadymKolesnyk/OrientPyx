namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The raw data for one participant statement («відомість»): the flat list of participant rows to print, in
/// no particular order (the builder sorts them by chip). Built in the DataAccess/BusinessLogic layer for a
/// given set of participant ids (the currently-shown rows) scoped to a single day or the whole roster; the
/// builder turns it + the user's <see cref="StatementSettings"/> into a renderable document.
/// </summary>
/// <param name="Rows">One entry per requested participant that was found. Rows missing a participant are dropped.</param>
/// <param name="DayLabels">The short header label for each competition day in the per-day «Старт» block, in day
/// order ("Д1", "Д2"…) — parallel to each row's <see cref="StatementRow.StartTimes"/>. One entry in day mode
/// (the scoped day) and one per day in roster mode. Empty when the competition has no days. Used by the builder
/// to expand the single logical <see cref="StatementColumn.Start"/> column into one physical column per day.</param>
public sealed record StatementData(
    IReadOnlyList<StatementRow> Rows,
    IReadOnlyList<string> DayLabels)
{
    public static StatementData Empty { get; } = new([], []);
}

/// <summary>
/// One participant's pre-resolved statement fields. All values are already formatted strings; per-day fields
/// (group, chip) are the day's value in day mode, or the distinct across-days values joined with " / " in
/// roster mode — matching the roster's collapsed-cell display. The chip sort keys (<see cref="HasOwnChip"/>,
/// <see cref="ChipSortKey"/>) drive the statement's fixed ordering: rental chips first, then own chips, then
/// by chip number ascending (chip-less last).
/// </summary>
public sealed class StatementRow
{
    public Guid ParticipantId { get; init; }

    public string Number { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public DateTimeOffset? BirthDate { get; init; }
    public string Group { get; init; } = string.Empty;
    public string Chip { get; init; } = string.Empty;

    /// <summary>The participant's start time on each competition day, as pre-formatted "hh:mm:ss" strings, in day
    /// order — parallel to <see cref="StatementData.DayLabels"/>. A day the participant does not run, or has no
    /// start time on, is an empty string (so the per-day «Старт» column prints blank for that day). One entry in
    /// day mode (the scoped day) and one per day in roster mode.</summary>
    public IReadOnlyList<string> StartTimes { get; init; } = [];
    public string Region { get; init; } = string.Empty;
    public string Club { get; init; } = string.Empty;
    public string Dussh { get; init; } = string.Empty;
    public string Coach { get; init; } = string.Empty;
    public string Rank { get; init; } = string.Empty;
    public string Team { get; init; } = string.Empty;
    public string Representative { get; init; } = string.Empty;
    public string FsouCode { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    /// <summary>True when the participant holds at least one chip that is NOT a rental chip (their own chip).
    /// Own-chip rows sort after rental-chip rows and print the chip cell in bold. False when the participant has
    /// no chip, or every chip they hold is a rental one.</summary>
    public bool HasOwnChip { get; init; }

    /// <summary>The chip number used to sort within the rental/own groups (the smallest chip number the
    /// participant holds), or <c>null</c> when they have no chip (sorted last). Numeric so "9" sorts before
    /// "80"; a non-numeric chip falls back to <see cref="int.MaxValue"/> - 1 so it lands just before the
    /// chip-less rows.</summary>
    public int? ChipSortKey { get; init; }

    /// <summary>True when the participant holds any chip at all (rental or own). Chip-less rows sort last.</summary>
    public bool HasChip { get; init; }
}
