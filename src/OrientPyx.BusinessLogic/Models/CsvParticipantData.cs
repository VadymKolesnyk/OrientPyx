namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Layer-neutral result of reading a participant CSV file: the raw header (column captions in file
/// order) and one string array per data row, aligned to the header. Pure data — no files opened, no
/// entities produced — so it can feed the column-mapping modal and then the import without coupling
/// the parser to storage. The columns carry whatever names the source file used; mapping them to our
/// known fields (<see cref="CsvParticipantField"/>) is the import flow's job.
/// </summary>
public sealed class CsvParticipantData
{
    /// <summary>The column captions, in file order. Used to build the mapping dropdowns.</summary>
    public IReadOnlyList<string> Header { get; init; } = [];

    /// <summary>One row per record; each is aligned to <see cref="Header"/> (short rows are padded blank).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
}

/// <summary>
/// The participant fields a CSV column can be mapped to. Mirrors the importable fields of
/// <see cref="UofParticipant"/> so a mapped CSV row can be turned into one. <see cref="None"/> means
/// "do not import this field"; it is the default for any of our fields the user leaves unmapped.
/// </summary>
public enum CsvParticipantField
{
    None = 0,
    FullName,
    Number,
    BirthDate,
    Group,
    Team,
    Region,
    Club,
    Dussh,
    Chip,
    Rank,
    Representative,
    FsouCode,
    IsFsouMember,
    Payment,
    Coach
}
