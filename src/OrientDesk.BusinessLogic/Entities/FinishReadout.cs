namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// One chip read at the finish on a given competition day — the persisted log of the finish read-out.
/// Per-day (a chip may be read on more than one day), append-only, and not edited by the user. The
/// readout file may carry the same chip several times (e.g. re-runs, multiple readers), so duplicates
/// are allowed; only an <b>identical</b> record (same chip + start/finish + punches, captured in
/// <see cref="ContentKey"/>) is treated as already-logged, so re-reading the same file never doubles rows.
/// </summary>
public class FinishReadout
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>
    /// Per-day 1-based sequence number, shown as the log's "Id" column and used as the stable sort key
    /// (SQLite cannot ORDER BY a DateTimeOffset column, so we never sort by the read times).
    /// </summary>
    public int Order { get; set; }

    /// <summary>Chip number as read. Trimmed.</summary>
    public string ChipNumber { get; set; } = string.Empty;

    /// <summary>Start time from the readout, when present; otherwise null.</summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>Finish time from the readout, when present; otherwise null.</summary>
    public DateTimeOffset? FinishTime { get; set; }

    /// <summary>
    /// Control codes the chip punched, in order, space-separated (e.g. "31 32 33"). The raw sequence
    /// the order-check compares against the group's prescribed course; empty when the file had none.
    /// </summary>
    public string Punches { get; set; } = string.Empty;

    /// <summary>
    /// Stable signature of the record's content (chip + start/finish + punch sequence) used to skip
    /// re-reading the same row. Two reads of the same physical read-out produce the same key.
    /// </summary>
    public string ContentKey { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
