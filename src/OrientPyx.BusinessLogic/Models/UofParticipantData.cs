using System.Text.Json.Serialization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Layer-neutral result of parsing a UOF participant file (the Ukrainian registration export,
/// <c>&lt;UOFData&gt;</c> root). Carries the competition-level organiser plus one
/// <see cref="UofParticipant"/> per <c>&lt;Sportsman&gt;</c>. Pure data — no files opened, no
/// entities produced — so it can feed the participant import without coupling the parser to storage.
/// </summary>
public sealed record UofParticipantData
{
    /// <summary>The organising body, from the file's top-level <c>&lt;Orgs&gt;</c> tag. May be blank.</summary>
    public string Organisation { get; init; } = string.Empty;

    public IReadOnlyList<UofParticipant> Participants { get; init; } = [];

    /// <summary>The additional fields the import fills from the records' <see cref="UofParticipant.Raw"/> values
    /// (an XML file's elements named like the field — chosen in the import window). Resolved into
    /// <see cref="UofParticipant.Fields"/> by the import itself; never sent over the network.</summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> FileFieldKeys { get; init; } = [];
}

/// <summary>
/// One athlete from a UOF file. Region/Club/Dussh/Group are the raw names as written in the file
/// (resolved to entities, created on demand, by the import). <see cref="DayNumbers"/> is the set of
/// 1-based day numbers the athlete competes on (parsed from <c>&lt;ProgEvent&gt;</c>, e.g. "1,2").
/// <see cref="Chip"/> is blank when the file had no chip or a literal "0".
/// </summary>
public sealed record UofParticipant
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

    /// <summary>Relay: the team number the runner runs for (Р-20); null when the file names no team number. Applied on
    /// the import's relay days only.</summary>
    public int? RelayTeamNumber { get; init; }

    /// <summary>Relay: the team name (used when the team is created, or to fill a blank name). With no
    /// <see cref="RelayTeamNumber"/> (UOF) the team is found by this exact name in the relay group on the day, else
    /// created without a number — the numbers are given later.</summary>
    public string RelayTeamName { get; init; } = string.Empty;

    /// <summary>Relay: the runner's leg; null ⇒ the team's next free leg (none free ⇒ on the team «без етапу»).</summary>
    public int? RelayLeg { get; init; }

    /// <summary>1-based day numbers the athlete runs (from ProgEvent). Empty means no days listed.</summary>
    public IReadOnlyList<int> DayNumbers { get; init; } = [];

    /// <summary>The days of <see cref="DayNumbers"/> the file marks as the runner's relay («3Е» in UOF ProgEvent): there
    /// the runner is in <see cref="RelayGroup"/> and runs for the team <see cref="RelayTeamName"/>.</summary>
    public IReadOnlyList<int> RelayDayNumbers { get; init; } = [];

    /// <summary>The relay group on <see cref="RelayDayNumbers"/> (UOF <c>GrupRel</c>, «МІКС12»); blank — no group on
    /// those days (the runner is entered there without one, to be sorted out by hand).</summary>
    public string RelayGroup { get; init; } = string.Empty;

    /// <summary>The group the runner is in on the day: <see cref="RelayGroup"/> on a relay day, else <see cref="Group"/>.</summary>
    public string GroupOn(int dayNumber) => RelayDayNumbers.Contains(dayNumber) ? RelayGroup : Group;

    /// <summary>Additional-field values by field key, as read from the file (normalised by the import, Р-18/Р-19).
    /// A blank value is "no information" and never clears a stored one. Empty for UOF files.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    /// <summary>Every simple value of the record as written in an XML file, by element / attribute name (UOF
    /// <c>&lt;Pol&gt;</c>, IOF <c>sex</c>) — what an additional field is filled from by its import names. Empty for
    /// CSV/Excel. Resolved by the import; never sent over the network.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Raw { get; init; } = new Dictionary<string, string>();
}
