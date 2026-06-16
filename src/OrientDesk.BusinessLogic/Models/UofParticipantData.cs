namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Layer-neutral result of parsing a UOF participant file (the Ukrainian registration export,
/// <c>&lt;UOFData&gt;</c> root). Carries the competition-level organiser plus one
/// <see cref="UofParticipant"/> per <c>&lt;Sportsman&gt;</c>. Pure data — no files opened, no
/// entities produced — so it can feed the participant import without coupling the parser to storage.
/// </summary>
public sealed class UofParticipantData
{
    /// <summary>The organising body, from the file's top-level <c>&lt;Orgs&gt;</c> tag. May be blank.</summary>
    public string Organisation { get; init; } = string.Empty;

    public IReadOnlyList<UofParticipant> Participants { get; init; } = [];
}

/// <summary>
/// One athlete from a UOF file. Region/Club/Dussh/Group are the raw names as written in the file
/// (resolved to entities, created on demand, by the import). <see cref="DayNumbers"/> is the set of
/// 1-based day numbers the athlete competes on (parsed from <c>&lt;ProgEvent&gt;</c>, e.g. "1,2").
/// <see cref="Chip"/> is blank when the file had no chip or a literal "0".
/// </summary>
public sealed class UofParticipant
{
    public string FullName { get; init; } = string.Empty;

    /// <summary>Bib / start number; blank when none. UOF files carry none, but CSV/Excel may.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>Team name (team disciplines); blank when none. UOF files carry none, but CSV/Excel may.</summary>
    public string Team { get; init; } = string.Empty;

    public string Representative { get; init; } = string.Empty;
    public string FsouCode { get; init; } = string.Empty;
    public bool IsFsouMember { get; init; }
    public DateTimeOffset? BirthDate { get; init; }
    public string Rank { get; init; } = string.Empty;
    public string Payment { get; init; } = string.Empty;

    /// <summary>All non-blank coaches, already joined with ", ".</summary>
    public string Coach { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;
    public string Club { get; init; } = string.Empty;
    public string Dussh { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;

    /// <summary>Chip number; blank when none (the file's "0"/empty is normalised to blank).</summary>
    public string Chip { get; init; } = string.Empty;

    /// <summary>1-based day numbers the athlete runs (from ProgEvent). Empty means no days listed.</summary>
    public IReadOnlyList<int> DayNumbers { get; init; } = [];
}
