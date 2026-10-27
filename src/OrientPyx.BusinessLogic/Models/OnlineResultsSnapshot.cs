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
    /// <summary>The published day's participant result rows. Only numbered participants are included — the
    /// frontend keys results by an integer bib, so an un-numbered runner cannot be published.</summary>
    IReadOnlyList<OnlineResultRow> Rows,
    /// <summary>How many of the day's participants were left out because they have no start number — they
    /// can't be addressed by the (event, bib, day) key. Surfaced as a warning in the publish log.</summary>
    int SkippedNoNumber = 0,
    /// <summary>Start numbers shared by several participants of the day, each as "181 (Name, Name)". Only the
    /// first holder is in <see cref="Rows"/> — the (event, bib, day) key can't carry two rows in one upsert.</summary>
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

    /// <summary>The plugin result columns carried by the legs' <see cref="OnlineLegRow.Extra"/>, in display order:
    /// key + label (a localization key until the caller translates it, see <c>docs/online-snapshot.md</c>).</summary>
    public IReadOnlyList<OnlineExtraColumn> ExtraColumns { get; init; } = [];
}

/// <summary>One extra (plugin) result column of the relay legs: its stable key and its spectator-facing label.</summary>
public sealed record OnlineExtraColumn(string Key, string Label);

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
    /// <summary>Plugin column values by <see cref="OnlineExtraColumn.Key"/>, already formatted.</summary>
    IReadOnlyDictionary<string, string> Extra);

/// <summary>One competition day for the frontend's day switcher: its number and a human label (e.g. "30 травня").</summary>
public sealed record OnlineDay(int Number, string Label);

/// <summary>One group on the published day: its name, course length (km), control count, and display order.</summary>
public sealed record OnlineGroup(string Name, decimal? DistanceKm, int? ControlCount, int Order);

/// <summary>
/// One participant's result on the published day, already computed. Maps onto the Supabase <c>results</c>
/// row: identity fields plus the place, times and score derived by the editor service. The publisher
/// formats times to strings and resolves the spectator-facing status code from <see cref="Status"/>.
/// </summary>
public sealed record OnlineResultRow(
    int? Bib,
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
    bool OutOfCompetition);
