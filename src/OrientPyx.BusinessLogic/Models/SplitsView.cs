using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Everything a discipline needs to build the passage/splits view for one selected read-out, all as
/// layer-neutral data. Mirrors <see cref="FinishContext"/> but additionally carries the per-control
/// point values (for the score formats) and the start time, so leg/elapsed splits can be computed.
/// The expected controls are the group's prescribed course already reduced to the codes that must be
/// visited (start/finish markers removed by the caller); <see cref="Punches"/> are the chip's actual
/// punches with their times (start/finish markers already excluded), in read order.
/// </summary>
public sealed class SplitsContext
{
    /// <summary>Control codes prescribed by the course, in order (no start/finish markers, with any
    /// disabled «проблемні» controls already removed by the caller — see <see cref="DisabledControls"/>).</summary>
    public IReadOnlyList<string> ExpectedControls { get; init; } = [];

    /// <summary>
    /// The group's raw course-order text as entered, needed by the «mixed» discipline to parse the order
    /// <b>pattern</b> (<c>&lt;…&gt;</c> / <c>[N …]</c> blocks) that <see cref="ExpectedControls"/> flattens
    /// away, so its ordered splits follow the pattern. Other layouts ignore it. Empty when none.
    /// </summary>
    public string CourseOrderText { get; init; } = string.Empty;

    /// <summary>
    /// Codes (trimmed) of prescribed controls that were marked disabled («проблемний КП») for the day and
    /// therefore dropped from <see cref="ExpectedControls"/>: they are no longer required and missing one is
    /// not penalised. The set-course ordered layout still lists them (flagged «вимкнено») so the operator
    /// sees the control was ignored. Empty when no control is disabled.
    /// </summary>
    public IReadOnlySet<string> DisabledControls { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Controls the chip actually punched, in read order (start/finish already excluded).</summary>
    public IReadOnlyList<ChipPunch> Punches { get; init; } = [];

    /// <summary>Point value per control code (score formats); missing/zero when not a scored control.</summary>
    public IReadOnlyDictionary<string, int> PointsByCode { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>WGS-84 position per control code, used to derive each leg's straight-line distance and
    /// pace. A code missing here (or with no coordinates) yields no distance for the legs touching it.
    /// Used only as a fallback when no paper-map position is available (see <see cref="MapByCode"/>).</summary>
    public IReadOnlyDictionary<string, GeoPoint> CoordsByCode { get; init; } =
        new Dictionary<string, GeoPoint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paper-map position (mm) per control code. Preferred over <see cref="CoordsByCode"/> for
    /// leg distances, since map mm × <see cref="MapScale"/> is undistorted by the geographic projection
    /// (the Web Mercator export stretches ground distance by 1/cos(latitude)).</summary>
    public IReadOnlyDictionary<string, MapPoint> MapByCode { get; init; } =
        new Dictionary<string, MapPoint>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Map scale denominator (e.g. 4000 for 1:4000) for the day's controls; null when unknown,
    /// in which case the leg distances fall back to the geographic coordinates.</summary>
    public int? MapScale { get; init; }

    /// <summary>Resolved start time (chip start, else assigned), used to time the first leg. Null = unknown.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Finish time from the read-out. Null = no finish punch.</summary>
    public DateTimeOffset? FinishTime { get; init; }

    /// <summary>Start point position, used for the first leg's distance (start → first control).</summary>
    public GeoPoint StartCoord { get; init; }

    /// <summary>Finish point position, used for the last leg's distance (last control → finish).</summary>
    public GeoPoint FinishCoord { get; init; }

    /// <summary>Start point map position (mm), preferred over <see cref="StartCoord"/> for the first leg.</summary>
    public MapPoint StartMap { get; init; }

    /// <summary>Finish point map position (mm), preferred over <see cref="FinishCoord"/> for the last leg.</summary>
    public MapPoint FinishMap { get; init; }

    /// <summary>The group's time limit (контрольний час) for the day, when set; null = no limit. Used by the
    /// scored formats (rogaine) to deduct an over-time penalty from the points total.</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>Points deducted per (started) minute of finishing late. For rogaine a null value means the
    /// default of 1 point/minute; for a discipline that doesn't penalise time it is ignored.</summary>
    public decimal? PenaltyPerMinute { get; init; }

    /// <summary>
    /// Codes (trimmed) that <b>every</b> member of the selected runner's rogaine team also punched, so the
    /// splits panel/printout can mark which controls count toward the team result (a rogaine control scores
    /// for the team only when the whole team visited it). Null when this isn't a team context — no team, a
    /// non-team discipline, or an unknown chip — in which case no team annotation is shown.
    /// </summary>
    public IReadOnlySet<string>? TeamCommonControls { get; init; }

    /// <summary>
    /// The scatter («розсіювання») course variants for the runner's group — each a valid order reduced to its
    /// required controls (start/finish and disabled controls already removed, like <see cref="ExpectedControls"/>).
    /// Populated only for a scatter group; empty otherwise. The scatter strategy picks the best-matching variant
    /// and builds the ordered passage against it.
    /// </summary>
    public IReadOnlyList<ScatterVariantData> ScatterVariants { get; init; } = [];
}

/// <summary>How the splits panel should render — chosen by the discipline.</summary>
public enum SplitsLayout
{
    /// <summary>Set course: prescribed order vs actual passage, each punch judged correct/wrong/extra.</summary>
    Ordered,

    /// <summary>Score / choice / rogaine: allowed controls visited-or-not with points and a running total.</summary>
    Scored
}

/// <summary>
/// The result a discipline produces for the selected read-out's splits panel. <see cref="Layout"/>
/// decides which shape is populated. <see cref="Ordered"/> (set course) renders two parallel lists —
/// <see cref="Passage"/> (every punch from the chip, in chip order) beside <see cref="Expected"/>
/// (the prescribed course in order) — so the actual route and the correct route are read side by side.
/// <see cref="Scored"/> renders <see cref="Entries"/> plus <see cref="TotalPoints"/>. Collections that
/// don't apply to the active layout are empty.
/// </summary>
public sealed class SplitsView
{
    public required SplitsLayout Layout { get; init; }

    /// <summary>
    /// Ordered layout — the actual passage: <b>every</b> punch read from the chip, in chip order,
    /// including out-of-order, foreign and repeated punches (nothing is dropped). Empty otherwise.
    /// </summary>
    public IReadOnlyList<PassagePunch> Passage { get; init; } = [];

    /// <summary>
    /// Ordered layout — the prescribed course in order, each control flagged taken or missing. The
    /// mapping to the actual passage. Empty otherwise.
    /// </summary>
    public IReadOnlyList<ExpectedControl> Expected { get; init; } = [];

    /// <summary>
    /// Codes (trimmed) of this course's controls that were marked disabled («проблемний КП») for the day and
    /// dropped from the required course — missing one is not penalised. The ordered layout lists them in
    /// <see cref="Expected"/> flagged <see cref="ExpectedControl.Ignored"/>; empty when none.
    /// </summary>
    public IReadOnlySet<string> DisabledControls { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Scored-layout rows: allowed controls in passage order, then unvisited ones.</summary>
    public IReadOnlyList<ScoreEntry> Entries { get; init; } = [];

    /// <summary>The result points — gross points collected <b>minus</b> the over-time penalty (never below 0);
    /// 0 for a non-scoring layout. This is the value that ranks the runner and shows as «Бали». Use
    /// <see cref="GrossPoints"/>/<see cref="Penalty"/> when the breakdown ("X − Y = Z") is needed.</summary>
    public int TotalPoints { get; init; }

    /// <summary>Points collected before the over-time penalty (the "X" in "X − Y = Z"). Equals
    /// <see cref="TotalPoints"/> when there is no penalty; 0 for a non-scoring layout.</summary>
    public int GrossPoints { get; init; }

    /// <summary>Over-time penalty deducted from <see cref="GrossPoints"/> (the "Y" in "X − Y = Z"): the
    /// minutes the finish ran past the time limit (rounded up) × the penalty rate. 0 when finished within
    /// the limit, no limit is set, or the discipline doesn't penalise time.</summary>
    public int Penalty { get; init; }

    /// <summary>
    /// True when this view scores points (rogaine), so the passage/course lists show the point columns and
    /// the summary includes the points total. A plain set-course ordered view is false.
    /// </summary>
    public bool HasPoints { get; init; }

    /// <summary>Count of controls visited that count toward the result (both layouts).</summary>
    public int VisitedCount { get; init; }

    /// <summary>Total controls expected/allowed by the course (both layouts).</summary>
    public int ExpectedCount { get; init; }

    /// <summary>
    /// For a scatter («розсіювання») course, the display code of the variant the runner was judged against
    /// (auto-detected as the best match — the closest one when nothing fully matched), e.g. "A". Empty for
    /// every non-scatter discipline. Shown in the splits panel and printed on the slip/HTML export.
    /// </summary>
    public string VariantCode { get; init; } = string.Empty;
}

/// <summary>What a passage row represents — a real control punch, or the start/finish markers.</summary>
public enum PassageKind
{
    /// <summary>A punched control (the default); judged on/off course.</summary>
    Control,

    /// <summary>The start marker — shown first, no on/off-course judgement.</summary>
    Start,

    /// <summary>The finish marker — shown last, no on/off-course judgement.</summary>
    Finish
}

/// <summary>
/// One punch in the actual passage (ordered layout): the control code as read, its time, the leg split
/// (time since the previous punch) and elapsed since the start. <see cref="OnCourse"/> is true when this
/// punch is the next prescribed control at the moment it was read (advancing the course pointer); false
/// for an out-of-order, foreign or repeated punch. <see cref="Kind"/> marks the synthetic start/finish
/// rows that bracket the controls (they carry no on/off-course glyph). <see cref="LegKm"/> is the
/// straight-line distance of this leg (from the previous punch) and <see cref="PaceSecondsPerKm"/> the
/// resulting tempo (leg time ÷ leg distance); both null when coordinates or a leg time are missing.
/// <see cref="CountsForTeam"/> is true for a rogaine scoring punch on a control the whole team also
/// punched (so it counts toward the team result); always false outside a team context.
/// <para>
/// <see cref="Leg"/>/<see cref="LegKm"/>/<see cref="PaceSecondsPerKm"/> are the <i>course</i> leg — the
/// true single prescribed leg (measured from the previous on-course control, skipping extras), only set
/// for a contiguous on-course punch — and feed the course total, the fastest-leg highlight and the HTML
/// split-table columns. <see cref="DisplayLeg"/>/<see cref="DisplayLegKm"/>/<see cref="DisplayPace"/> are
/// the <i>row-to-row</i> leg measured from the immediately preceding punch in chip order, so they are
/// filled for <b>every</b> control (including extras/off-course) and the finish; the read-out panel and
/// the printout slip show these so each row keeps its time/distance columns rather than blanking out.
/// </para>
/// </summary>
public sealed record PassagePunch(
    int Index,
    string Code,
    bool OnCourse,
    DateTimeOffset? Time,
    TimeSpan? Leg,
    TimeSpan? Elapsed,
    PassageKind Kind = PassageKind.Control,
    decimal? LegKm = null,
    double? PaceSecondsPerKm = null,
    int? Points = null,
    int? RunningTotal = null,
    bool CountsForTeam = false,
    TimeSpan? DisplayLeg = null,
    decimal? DisplayLegKm = null,
    double? DisplayPace = null);

/// <summary>
/// One control of the prescribed course (ordered layout): its 1-based order, its code, and whether the
/// chip took it (<see cref="Taken"/> false = a missing control). For a scored (rogaine) layout the
/// "course" is the allowed-control set sorted by code and <see cref="Points"/> carries the control's value.
/// <see cref="CountsForTeam"/> is true when the whole rogaine team punched this control (it scores for the
/// team); always false outside a team context.
/// </summary>
public sealed record ExpectedControl(int Sequence, string Code, bool Taken, int? Points = null, bool CountsForTeam = false, bool Ignored = false);

/// <summary>
/// One row of the scored (score/choice/rogaine) splits panel: an allowed control, whether it was
/// visited, its point value, the punch time when visited, and the running total after this control.
/// Unvisited allowed controls have <see cref="Visited"/> false and no time.
/// </summary>
public sealed record ScoreEntry(
    string Code,
    bool Visited,
    int Points,
    DateTimeOffset? PunchTime,
    TimeSpan? Elapsed,
    int RunningTotal);
