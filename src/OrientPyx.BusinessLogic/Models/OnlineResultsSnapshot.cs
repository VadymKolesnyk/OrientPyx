using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One publish tick's worth of data for the online live-results service: the competition's metadata, all of
/// its days (so the spectator frontend can build the day switcher), and — for the one day being published —
/// its groups and computed result rows. Gathered by
/// <see cref="Interfaces.ICompetitionEditorService.GetOnlineResultsSnapshotAsync"/> from the same computed
/// results the protocols use, then turned into Supabase rows by <c>IResultPublisher</c>. Layer-neutral: no
/// EF Core, no HTTP — just the shape the publisher upserts.
/// </summary>
public sealed record OnlineResultsSnapshot(
    /// <summary>Every day of the competition (number + label) — metadata for the frontend's day switcher.</summary>
    IReadOnlyList<OnlineDay> Days,
    /// <summary>The 1-based number of the day whose <see cref="Groups"/> are in this snapshot.</summary>
    int PublishedDayNumber,
    /// <summary>The published day's groups (name, course metadata, display order).</summary>
    IReadOnlyList<OnlineGroup> Groups,
    /// <summary>The published day's participant result rows (personal groups) — every runner, numbered or not: the
    /// frontend keys results by an integer bib, so a runner without one goes under a stand-in key
    /// (<see cref="OnlineResultRow.Key"/>).</summary>
    IReadOnlyList<OnlineResultRow> Rows,
    /// <summary>How many of <see cref="Rows"/> have no start number of their own (published under a stand-in key,
    /// shown without a number). Surfaced as a note in the publish log.</summary>
    int Unnumbered = 0,
    /// <summary>Start numbers shared by several participants of the day, each as "181 (Name, Name)". Only the
    /// first holder is published under the number — the (event, bib, day) key can't carry two rows in one upsert —
    /// the others go without one, like an un-numbered runner.</summary>
    IReadOnlyList<string>? DuplicateNumbers = null)
{
    /// <summary>An empty snapshot (no day selected / nothing to publish).</summary>
    public static readonly OnlineResultsSnapshot Empty = new([], 0, [], []);

    /// <summary>True when there is a day to publish.</summary>
    public bool HasData => PublishedDayNumber > 0;

    /// <summary>Relay teams of the published day (§11.1) — one per team of a relay group, with its legs. Empty for a
    /// day without relay groups, so a personal competition's snapshot is unchanged. A relay runner's leg number
    /// («100.2») is not an integer bib, so relay groups publish through these rows, not through <see cref="Rows"/>.</summary>
    public IReadOnlyList<OnlineTeamRow> Teams { get; init; } = [];

    /// <summary>The day's extra result columns (§10.2) — plugin columns, then additional participant fields — whose
    /// values the rows' <see cref="OnlineResultRow.Extra"/> and the legs' <see cref="OnlineLegRow.Extra"/> carry, in
    /// display order: key + label (a localization key until the caller translates it, see
    /// <c>docs/online-snapshot.md</c>).</summary>
    public IReadOnlyList<OnlineExtraColumn> ExtraColumns { get; init; } = [];
}

/// <summary>One extra result column of the day: its stable key (<c>"&lt;pluginId&gt;.&lt;key&gt;"</c> or
/// <c>"field.&lt;key&gt;"</c>), its spectator-facing label, and whether a plugin declared it (a plugin column is shown
/// online until switched off; an additional field only once switched on).</summary>
public sealed record OnlineExtraColumn(string Key, string Label, bool IsPlugin = true);

/// <summary>One relay team on the published day (§11.1): identity, computed team result and its legs in order.</summary>
public sealed record OnlineTeamRow(
    string GroupName,
    int Number,
    string Name,
    string Club,
    string Region,
    /// <summary>Team place in the group; null when unplaced (not OK, «поза конкурсом», unranked).</summary>
    int? Place,
    /// <summary>Team time Σ adjusted leg times (Р-32); null until every leg has a time.</summary>
    TimeSpan? Time,
    FinishStatus Status,
    bool OutOfCompetition,
    bool Restarted,
    decimal? Points,
    IReadOnlyList<OnlineLegRow> Legs);

/// <summary>One leg of an <see cref="OnlineTeamRow"/>; a leg nobody runs has a blank <see cref="FullName"/>.</summary>
public sealed record OnlineLegRow(
    int Leg,
    /// <summary>The leg number «100.2» (Р-23).</summary>
    string Bib,
    string FullName,
    string Birth,
    /// <summary>The leg's course code (Р-44), or null when the group runs one course.</summary>
    string? Course,
    TimeSpan? LegTime,
    TimeSpan? Cumulative,
    FinishStatus Status,
    bool Pending,
    bool Restarted,
    /// <summary>Extra column values (plugin columns and additional fields) by <see cref="OnlineExtraColumn.Key"/>,
    /// already formatted; blank values left out.</summary>
    IReadOnlyDictionary<string, string> Extra)
{
    /// <summary>The leg's counted time («Результат»): adjusted by the discipline (weighted relay: T·K), else
    /// <see cref="LegTime"/>, which stays the clean time («Час»).</summary>
    public TimeSpan? Result { get; init; }

    /// <summary>The leg's start, time of day (leg 1 — the start, leg N — the handoff or the restart).</summary>
    public TimeSpan? StartTime { get; init; }

    public string Qual { get; init; } = string.Empty;

    public string Club { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;
}

/// <summary>One competition day for the frontend's day switcher: its number and a human label (e.g. "30 травня").</summary>
public sealed record OnlineDay(int Number, string Label);

/// <summary>One group on the published day: its name, course length (km), control count, and display order.</summary>
public sealed record OnlineGroup(string Name, decimal? DistanceKm, int? ControlCount, int Order);

/// <summary>
/// One participant's result on the published day, already computed. Maps onto the Supabase <c>results</c>
/// row: identity fields plus the place, times and score derived by the editor service. The publisher
/// formats times to strings and resolves the spectator-facing status code from <see cref="Status"/>.
/// </summary>
/// <param name="Key">The row's <c>results.bib</c>: the runner's start number (positive), or for a runner without one
/// a negative stand-in, stable for the participant across days and machines (<see cref="UnnumberedKey"/>). The site
/// shows only a positive bib.</param>
public sealed record OnlineResultRow(
    int Key,
    string GroupName,
    string FullName,
    string Team,
    string Club,
    string Region,
    string Birth,
    string Qual,
    /// <summary>1-based place within the group on the day, or null when unplaced.</summary>
    int? Place,
    /// <summary>The clean result time, or null when there is none.</summary>
    TimeSpan? ResultTime,
    /// <summary>The chip's actual start time of day, or null.</summary>
    TimeSpan? StartTime,
    /// <summary>The chip's finish time of day, or null.</summary>
    TimeSpan? FinishTime,
    /// <summary>Score points for a point-scoring (rogaine) discipline, or null.</summary>
    int? Score,
    /// <summary>Ranking points («Бали»/«Очки») for the result, or null.</summary>
    decimal? Points,
    /// <summary>The effective finish status, mapped by the publisher to a spectator status code.</summary>
    FinishStatus Status,
    /// <summary>True when no read-out matched this participant's chip yet (drives running vs dns).</summary>
    bool HasReadout,
    /// <summary>True for an «поза конкурсом» (out-of-competition) personal run — never placed.</summary>
    bool OutOfCompetition)
{
    /// <summary>Extra column values (plugin columns and additional fields) by <see cref="OnlineExtraColumn.Key"/>,
    /// already formatted; blank values left out.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();

    /// <summary>The start number shown for the runner; null when published under a stand-in key.</summary>
    public int? Bib => Key > 0 ? Key : null;

    /// <summary>The stand-in <c>results.bib</c> of a runner without a start number: a negative number derived from the
    /// participant id, so the same runner keeps it on every day (the site sums days by bib) and on every machine (a
    /// station reads the host's ids). Never collides with a real number; two runners of one day landing on the same
    /// value (rare: 31 bits of the id) are told apart by the snapshot.</summary>
    public static int UnnumberedKey(Guid participantId)
    {
        Span<byte> bytes = stackalloc byte[16];
        participantId.TryWriteBytes(bytes);
        return -1 - (int)(BitConverter.ToUInt32(bytes) % int.MaxValue);
    }
}
