using OrientPyx.BusinessLogic.Disciplines;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>Which IOF 3.0 list the export writes.</summary>
public enum IofListKind
{
    /// <summary><c>&lt;ResultList&gt;</c> — results with splits.</summary>
    ResultList,

    /// <summary><c>&lt;StartList&gt;</c> — the start list.</summary>
    StartList
}

/// <summary>Which affiliation fills a runner's single IOF <c>&lt;Organisation&gt;</c>.</summary>
public enum IofOrganisationSource
{
    /// <summary>The club; the region when the runner has no club.</summary>
    Club,

    /// <summary>The region (область); the club when the runner has no region.</summary>
    Region
}

/// <summary>
/// The raw data of an IOF XML export, gathered by the editor service from the same computed results the protocols
/// and the online snapshot use: the competition, and per exported day its protocol data, its groups' strategy answers
/// and its runners' extras. The layer-neutral <c>IIofExportBuilder</c> turns it into an <see cref="IofResultList"/> or
/// <see cref="IofStartList"/>; it knows nothing of strategies, files or the database.
/// </summary>
/// <param name="Days">The exported days in day-number order; one for a single-day file.</param>
/// <param name="Summary">The multi-day summary data (results of every day), for the «по сумі днів» totals of a
/// multi-day ResultList; null otherwise.</param>
/// <param name="SummarySettings">The «Підсумковий протокол» settings the totals follow; null = the defaults (the page
/// fills the app-level default in when the competition has none, as the summary page does).</param>
public sealed record IofExportData(
    IofEventData Event,
    IReadOnlyList<IofDayExport> Days,
    SummaryProtocolData? Summary = null,
    SummaryProtocolSettings? SummarySettings = null)
{
    public static readonly IofExportData Empty = new(new IofEventData(string.Empty, string.Empty, 0), []);
}

/// <summary>The competition: its name, organiser and how many days it has.</summary>
public sealed record IofEventData(string CompetitionName, string Organiser, int DayCount);

/// <summary>One exported day (an IOF race): its number, the date the times are written on, the offset of that date
/// and whether the day is closed (<c>EventDay.IsLocked</c> — decides «not started yet» vs «did not start»).</summary>
public sealed record IofRaceData(Guid DayId, int DayNumber, DateOnly Date, TimeSpan Offset, bool IsLocked);

/// <summary>
/// One day of the export: the day's result protocol data (ResultList) or start protocol data (StartList), the
/// strategy answers of every group of the day by group name (case- and homoglyph-insensitive), and every runner's
/// extras by participant id.
/// </summary>
public sealed record IofDayExport(
    IofRaceData Race,
    ResultProtocolData? Results,
    StartProtocolData? Starts,
    IReadOnlyDictionary<string, IofClassInfo> Classes,
    IReadOnlyDictionary<Guid, IofRunnerData> Runners);

/// <summary>
/// What a group's discipline says, collected by the editor so the builder never asks a strategy: the course length
/// and control count the protocol prints, the competitor model, whether it scores points and whether its relay legs
/// run named courses (then the class has no single course).
/// </summary>
public sealed record IofClassInfo(
    decimal? DistanceKm,
    int? ControlCount,
    CompetitorModel Competitors,
    bool UsesScore,
    bool HasLegCourses);

/// <summary>
/// A runner's extras on a day: the FSOU code, the chip, the assigned start, the start the discipline resolved for the
/// read-out (null without one — a relay leg uses its leg start instead), the splits (null without a read-out) and the
/// named course they ran (scatter variant, relay leg course), if any.
/// </summary>
public sealed record IofRunnerData(
    string FsouCode,
    string Chip,
    TimeSpan? AssignedStart,
    DateTimeOffset? ResolvedStart,
    SplitsView? Splits,
    IofRunnerCourse? Course);

/// <summary>A runner's named course: its code and, when the course file named it, the name and family it gave.</summary>
public sealed record IofRunnerCourse(string Code, string? SourceName, string? SourceFamily, int? ControlCount);

/// <summary>
/// The export's options: the groups to write (null = all), the affiliation source, birth dates, the event name and the
/// message header values (fixed in tests so the output is byte-stable). <see cref="DayLabel"/> names a race in a
/// multi-day file («День» → «День 2»).
/// </summary>
public sealed record IofExportOptions(
    IReadOnlySet<string>? Groups,
    IofOrganisationSource Organisation,
    bool IncludeBirthDates,
    string EventName,
    string Creator,
    DateTimeOffset CreateTime,
    string DayLabel = "День");

/// <summary>
/// The saved options of the «Експорт IOF XML» page, per competition (<see cref="Entities.IofExportSettingsRow"/>).
/// The group choice is not kept — it depends on the day.
/// </summary>
public sealed class IofExportSettings
{
    public IofOrganisationSource Organisation { get; set; } = IofOrganisationSource.Club;

    /// <summary>Birth dates are written by default: the ranking needs them.</summary>
    public bool IncludeBirthDates { get; set; } = true;

    /// <summary>Several days in one file (Event/Race + <c>raceNumber</c>) instead of one day.</summary>
    public bool MultiDay { get; set; }

    /// <summary>The days ticked for a multi-day file; empty = every day.</summary>
    public List<Guid> DayIds { get; set; } = [];
}
