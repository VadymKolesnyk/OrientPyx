using System.ComponentModel;

namespace OrientPyx.Plugins.Sdk;

/// <summary>What a report is built for.</summary>
public enum ReportScope
{
    /// <summary>One day, picked on the report's page (the session day by default).</summary>
    Day,

    /// <summary>The whole competition: every day at once.</summary>
    Competition
}

/// <summary>
/// A plugin report (API 2): an item of the plugin's «Плагіни» menu that opens a page showing a document the plugin
/// builds from the competition's data — a team ranking, a check list, a special protocol. The application shows the
/// document on screen and exports it to Word with the same header, signatures and footer as its own protocols; the
/// plugin only decides the columns and the rows. Registered with <see cref="IPluginBuilder.AddReport{T}"/>.
/// </summary>
/// <remarks>Like a <see cref="Discipline"/>: one instance for the whole run, no state in fields, no files or
/// network — <see cref="Build"/> gets everything in its context and returns a new document each time.</remarks>
public abstract class Report
{
    /// <summary>Stable id, conventionally <c>"&lt;pluginId&gt;.&lt;name&gt;"</c>; the report's saved settings are
    /// kept under it.</summary>
    public abstract string Id { get; }

    /// <summary>The menu item and page title — a key of the plugin's strings, or plain text.</summary>
    public abstract string Name { get; }

    /// <summary>A line under the page title (a key of the plugin's strings, or plain text); null for none.</summary>
    public virtual string? Description => null;

    /// <summary>One day (picked on the page) or the whole competition.</summary>
    public virtual ReportScope Scope => ReportScope.Day;

    /// <summary>The report's own settings, edited above the document (an auto-built form, like a discipline's). A
    /// <see cref="SettingScope.Day"/> setting of a <see cref="ReportScope.Day"/> report is kept per day; everything
    /// else once per competition.</summary>
    public virtual IEnumerable<SettingDef> Settings => [];

    /// <summary>Builds the document from the competition's data. An exception is shown on the page instead of the
    /// document.</summary>
    public abstract ReportDocument Build(ReportContext ctx);
}

/// <summary>What <see cref="Report.Build"/> sees: the competition's participants and computed results, read when
/// the page is built (the same results the protocols show).</summary>
public sealed class ReportContext
{
    /// <summary>The competition's name.</summary>
    public string CompetitionName { get; init; } = string.Empty;

    /// <summary>The days the report covers, in day order: the picked day alone for a <see cref="ReportScope.Day"/>
    /// report, every day for a <see cref="ReportScope.Competition"/> one.</summary>
    public IReadOnlyList<ReportDay> Days { get; init; } = [];

    /// <summary>The picked day of a <see cref="ReportScope.Day"/> report; null for a competition report.</summary>
    public ReportDay? Day { get; init; }

    /// <summary>Every participant of the competition (whether or not on a day), by name.</summary>
    public IReadOnlyList<ParticipantView> Participants { get; init; } = [];

    /// <summary>The report's setting values (<see cref="Report.Settings"/>).</summary>
    public SettingValues Settings { get; init; } = SettingValues.Empty;

    /// <summary>Resolves the plugin's string keys (supplied by the host).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public Func<string, string>? Strings { get; init; }

    public T Setting<T>(string key) => Settings.Get<T>(key);

    /// <summary>A plugin string in the language of the program (the key itself when it has no translation) — for
    /// texts the plugin puts in cells, section titles and notes, which are shown as given.</summary>
    public string Text(string key) => Strings?.Invoke(key) ?? key;
}

/// <summary>One competition day with its groups and computed results.</summary>
public sealed class ReportDay
{
    /// <summary>The day number (1, 2 …).</summary>
    public int Number { get; init; }

    /// <summary>The day's date; null when not set.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>The day's discipline id (a built-in name like <c>SetCourse</c> or a plugin's <see cref="Discipline.Id"/>).</summary>
    public string Discipline { get; init; } = string.Empty;

    /// <summary>The groups running on the day, in the day's group order.</summary>
    public IReadOnlyList<ReportGroup> Groups { get; init; } = [];
}

/// <summary>One group on a day.</summary>
public sealed class ReportGroup
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The group's discipline id on the day (its own, else the day's).</summary>
    public string Discipline { get; init; } = string.Empty;

    /// <summary>Every participant of the group on the day with their result — placed ones first by place, then the
    /// rest by name. In a relay group: every runner, with the team's place and points.</summary>
    public IReadOnlyList<ReportResult> Results { get; init; } = [];

    /// <summary>The relay teams of the group (in a relay discipline), placed first by place; empty otherwise.</summary>
    public IReadOnlyList<ReportTeam> Teams { get; init; } = [];
}

/// <summary>One participant's computed result on a day.</summary>
public sealed class ReportResult
{
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    public ParticipantView Participant { get; init; } = new();

    /// <summary>The effective status (a judge's override included); <see cref="FinishStatus.None"/> while not read out.</summary>
    public FinishStatus Status { get; init; }

    /// <summary>The actual time (for a relay leg: the leg time); null when there is none.</summary>
    public TimeSpan? Time { get; init; }

    /// <summary>The time the result is ranked by (after <see cref="Discipline.AdjustTime"/>); null when not OK.</summary>
    public TimeSpan? AdjustedTime { get; init; }

    /// <summary>Points «Бали» of a scoring format; null otherwise.</summary>
    public int? Score { get; init; }

    /// <summary>The place in the group (a relay runner: the team's); null when not placed.</summary>
    public int? Place { get; init; }

    /// <summary>Ranking points «Очки» from the group's points rule (a relay runner: the team's); null when none.</summary>
    public decimal? Points { get; init; }

    /// <summary>«Поза конкурсом».</summary>
    public bool OutOfCompetition { get; init; }

    /// <summary>Why a discipline left the result unranked (<see cref="ResultRow.MarkUnranked"/>, the key qualified
    /// with its plugin's id); null when ranked.</summary>
    public string? UnrankedReason { get; init; }

    /// <summary>The sports rank the result earned («виконаний розряд»); null when none.</summary>
    public string? AwardedRank { get; init; }

    /// <summary>The relay team number (0 while the team has no number); null outside a relay.</summary>
    public int? TeamNumber { get; init; }

    /// <summary>The relay leg the participant runs; null outside a relay (or on a team without a leg).</summary>
    public int? Leg { get; init; }

    /// <summary>The values a discipline set on the result (<see cref="ResultRow.Set"/>), by key.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = NoValues;

    public T? Get<T>(string key) =>
        Values.TryGetValue(key, out var value) ? SettingValues.ConvertTo<T>(value) : default;
}

/// <summary>One relay team on a day.</summary>
public sealed class ReportTeam
{
    /// <summary>The team number (100 of «100.2»); 0 while the team has no number.</summary>
    public int Number { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Club { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;

    public FinishStatus Status { get; init; }

    /// <summary>The team time (the sum of the adjusted leg times); null until every leg has one.</summary>
    public TimeSpan? Time { get; init; }

    public int? Place { get; init; }

    public decimal? Points { get; init; }

    public bool OutOfCompetition { get; init; }

    /// <summary>The runners by leg (index 0 = leg 1); null where nobody runs the leg.</summary>
    public IReadOnlyList<ReportResult?> Legs { get; init; } = [];
}

/// <summary>
/// The document a report returns: a title, the columns, and one or more sections (a caption + rows), laid out like
/// the application's protocols. Text in it is shown as given — translate it with <see cref="ReportContext.Text"/>;
/// column labels may also be keys of the plugin's strings.
/// </summary>
public sealed class ReportDocument
{
    /// <summary>The bold title under the competition name; blank — the report's <see cref="Report.Name"/>.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>A line printed under the title (with the date and the venue); blank for none.</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Landscape pages.</summary>
    public bool Landscape { get; init; }

    /// <summary>Print the competition's officials (chief judge, secretary, jury) at the end, as the protocols do.</summary>
    public bool Officials { get; init; } = true;

    /// <summary>The columns, shared by every section; each row has one cell per column.</summary>
    public IReadOnlyList<ReportColumn> Columns { get; init; } = [];

    public IReadOnlyList<ReportSection> Sections { get; init; } = [];
}

/// <summary>A column of a <see cref="ReportDocument"/>. <see cref="Label"/> is a key of the plugin's strings or
/// plain text.</summary>
public sealed record ReportColumn(string Label)
{
    /// <summary>Free text that may wrap onto several lines (names, clubs); short values (places, times) never wrap.</summary>
    public bool Wrap { get; init; }
}

/// <summary>One block of a <see cref="ReportDocument"/>: a bold caption (a group, a ranking), an optional line
/// under it, the rows and an optional note under the table.</summary>
public sealed class ReportSection
{
    public string Title { get; init; } = string.Empty;

    /// <summary>A line under the caption; blank for none.</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>A line under the table; blank for none.</summary>
    public string Note { get; init; } = string.Empty;

    public IReadOnlyList<ReportRow> Rows { get; init; } = [];
}

/// <summary>How a row is drawn.</summary>
public enum ReportRowKind
{
    /// <summary>A plain row.</summary>
    Normal,

    /// <summary>A bold caption row over the rows that follow (a team over its members).</summary>
    Caption,

    /// <summary>A second-level row (a member under its team): indented, smaller.</summary>
    Sub
}

/// <summary>
/// One row of a <see cref="ReportSection"/>: a cell per column. A cell may be any value — <c>null</c> is blank, a
/// <see cref="TimeSpan"/> prints as a result time («0:42:15»), a <see cref="FinishStatus"/> as its code («MP»), a
/// number in the program's format; anything else as text.
/// </summary>
public sealed class ReportRow
{
    public ReportRow(params object?[] cells) => Cells = cells;

    public IReadOnlyList<object?> Cells { get; }

    public ReportRowKind Kind { get; init; } = ReportRowKind.Normal;

    /// <summary>A bold caption row (<see cref="ReportRowKind.Caption"/>).</summary>
    public static ReportRow Caption(params object?[] cells) => new(cells) { Kind = ReportRowKind.Caption };

    /// <summary>A second-level row (<see cref="ReportRowKind.Sub"/>).</summary>
    public static ReportRow Sub(params object?[] cells) => new(cells) { Kind = ReportRowKind.Sub };
}
