namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Layer-neutral result of reading a chip-readout file (e.g. a SPORTident punch-log export).
/// Produced by <see cref="Interfaces.IReadoutParser"/> and independent of any concrete file
/// format, database entity, or UI type, so the same output can feed several consumers: today the
/// rental-chip database (which uses only the chip numbers), and later participant timing/results.
/// </summary>
public sealed class ChipReadData
{
    /// <summary>Identifier of the format the records were read from, e.g. "SportIdent-CSV".</summary>
    public string SourceFormat { get; init; } = string.Empty;

    /// <summary>All read-out records found in the file, in file order. May contain duplicate chip numbers.</summary>
    public IReadOnlyList<ChipReadRecord> Records { get; init; } = [];
}

/// <summary>
/// One chip read-out: the chip number plus, when the file carried them, the start/finish times
/// and the controls the chip punched. Only the chip number is guaranteed; everything else is
/// optional because a given file (or row) may not record it.
/// </summary>
public sealed class ChipReadRecord
{
    /// <summary>Chip number as written in the file. Trimmed.</summary>
    public string ChipNumber { get; init; } = string.Empty;

    /// <summary>Start time, when the file recorded one; otherwise null.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Finish time, when the file recorded one; otherwise null.</summary>
    public DateTimeOffset? FinishTime { get; init; }

    /// <summary>Controls punched in order, when the file recorded them. Empty otherwise.</summary>
    public IReadOnlyList<ChipPunch> Punches { get; init; } = [];
}

/// <summary>A single control punch read from a chip: the control code and, when known, its time.</summary>
/// <param name="ControlCode">Control code/number as written in the file. Trimmed.</param>
/// <param name="Time">Punch time when the file carried one; otherwise null.</param>
public sealed record ChipPunch(string ControlCode, DateTimeOffset? Time);
