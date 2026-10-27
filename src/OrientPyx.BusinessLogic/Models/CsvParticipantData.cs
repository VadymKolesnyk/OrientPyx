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
/// What a CSV/Excel column can be mapped to: one of the built-in participant fields (the static instances below,
/// keyed by their historic names), an additional field (<c>field.&lt;key&gt;</c>, see <see cref="Field"/>), or — from
/// the relay phase — a relay target (<c>relay.*</c>). A record keyed by a string so new kinds of target need no enum
/// change and a saved mapping template survives new targets being added.
/// </summary>
public sealed record ImportTarget(string Key)
{
    public static readonly ImportTarget FullName = new(nameof(FullName));
    public static readonly ImportTarget Number = new(nameof(Number));
    public static readonly ImportTarget BirthDate = new(nameof(BirthDate));
    public static readonly ImportTarget Group = new(nameof(Group));
    public static readonly ImportTarget Team = new(nameof(Team));
    public static readonly ImportTarget Region = new(nameof(Region));
    public static readonly ImportTarget Club = new(nameof(Club));
    public static readonly ImportTarget Dussh = new(nameof(Dussh));
    public static readonly ImportTarget Chip = new(nameof(Chip));
    public static readonly ImportTarget Rank = new(nameof(Rank));
    public static readonly ImportTarget Representative = new(nameof(Representative));
    public static readonly ImportTarget FsouCode = new(nameof(FsouCode));
    public static readonly ImportTarget IsFsouMember = new(nameof(IsFsouMember));
    public static readonly ImportTarget Payment = new(nameof(Payment));
    public static readonly ImportTarget Coach = new(nameof(Coach));

    /// <summary>The built-in targets in the order the mapping modal lists them; FullName leads (it is required).</summary>
    public static IReadOnlyList<ImportTarget> Builtins { get; } =
    [
        FullName, Number, BirthDate, Group, Team, Region, Club, Dussh, Chip, Rank, Representative, FsouCode,
        IsFsouMember, Payment, Coach
    ];

    /// <summary>Relay team number (100 of «100.2»).</summary>
    public static readonly ImportTarget RelayTeamNumber = new("relay.teamNumber");

    /// <summary>Relay team name.</summary>
    public static readonly ImportTarget RelayTeamName = new("relay.teamName");

    /// <summary>Relay leg (2 of «100.2»).</summary>
    public static readonly ImportTarget RelayLeg = new("relay.leg");

    /// <summary>The relay targets, listed by the mapping modal when the day runs a relay.</summary>
    public static IReadOnlyList<ImportTarget> Relay { get; } = [RelayTeamNumber, RelayTeamName, RelayLeg];

    /// <summary>The target of an additional participant field.</summary>
    public static ImportTarget Field(string fieldKey) => new(CustomField.ColumnPrefix + fieldKey);

    /// <summary>The additional field's key when this targets one; null otherwise.</summary>
    public string? FieldKey => Key.StartsWith(CustomField.ColumnPrefix, StringComparison.Ordinal)
        ? Key[CustomField.ColumnPrefix.Length..]
        : null;

    public bool IsBuiltin => Builtins.Contains(this);

    public override string ToString() => Key;
}
