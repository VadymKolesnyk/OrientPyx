namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A ready-to-render split export for one day: the header text plus a section per group, each carrying its
/// discipline layout and its members' splits. Layer-neutral and already localized — the writer
/// (<c>ISplitHtmlWriter</c>, in DataAccess) only turns this into an HTML document, mirroring how the
/// result protocol's <see cref="ResultProtocolDocument"/> feeds the .docx writer.
/// </summary>
public sealed record SplitExportDocument(
    string Title,
    string Subtitle,
    string CompetitionType,
    string Venue,
    string DateText,
    IReadOnlyList<SplitExportGroup> Groups,
    SplitExportLabels Labels);

/// <summary>
/// One group's section of the split export: its name, course metadata, the layout to render, and its
/// member rows (already ordered placed-first by the builder). <see cref="Layout"/> picks the writer's
/// shape — a set-course table (per-control columns) vs a free-order per-runner passage.
/// </summary>
public sealed record SplitExportGroup(
    string Name,
    SplitsLayout Layout,
    /// <summary>Course length in km, or null when unknown.</summary>
    decimal? DistanceKm,
    /// <summary>Running control count on the course, or null when unknown.</summary>
    int? ControlCount,
    /// <summary>
    /// For a set-course group, the prescribed running controls in order (codes, no start/finish) — the
    /// table's column set. Empty for the scored layout, which lists each runner's own passage instead.
    /// </summary>
    IReadOnlyList<string> Controls,
    /// <summary>True when the group scores points (rogaine), so the writer shows the points columns.</summary>
    bool HasPoints,
    IReadOnlyList<SplitExportRow> Rows);

/// <summary>
/// One participant's split row: their identity and computed result plus the discipline-built
/// <see cref="SplitsView"/> (the passage/course already laid out). The writer reads the splits for the
/// per-control times; the result fields fill the header columns (number, name, result, place, status).
/// </summary>
public sealed record SplitExportRow(
    string Number,
    string FullName,
    string Team,
    /// <summary>Formatted result (time or «бали»), already localized; blank for a non-OK status.</summary>
    string ResultText,
    /// <summary>Formatted place ("1", "2", …) or blank when unplaced.</summary>
    string PlaceText,
    /// <summary>Status text shown for a problem result (DNF/MP/DSQ/…); blank for an OK result.</summary>
    string StatusText,
    /// <summary>True for an OK (placed/ranked) result — drives row styling.</summary>
    bool IsOk,
    /// <summary>
    /// For a scored (rogaine) OK result, the penalty/bonus detail shown under the «Бали» total in the result
    /// cell (e.g. "−3" penalty and "+2" bonus), or empty when neither applies. Lets the cell spell out how
    /// the points net out, matching the participant tables. Blank for a time result or a non-OK status.
    /// </summary>
    string ResultDetail,
    /// <summary>
    /// The «Бали» breakdown tooltip (multi-line) shown as the result cell's <c>title</c> — identical to the
    /// participants table's score tooltip (per-control points, then gross/penalty/bonus/total). Empty when
    /// there is no scored breakdown (no tooltip then).
    /// </summary>
    string ResultTooltip,
    SplitsView Splits);

/// <summary>
/// Localized labels and captions the split writer needs, gathered once by the page so the writer
/// (a different layer) stays free of <c>ILocalizationService</c>.
/// </summary>
public sealed record SplitExportLabels(
    string DefaultTitle,
    string ColumnPlace,
    string ColumnName,
    string ColumnNumber,
    string ColumnResult,
    string ColumnFinish,
    string ColumnScore,
    string ControlPrefix,
    string DistanceLabel,
    /// <summary>Header for the scored-table per-runner "distance run by chip" column (e.g. "Дист., км").</summary>
    string ColumnDistance,
    string ControlCountLabel,
    string GeneratedLabel,
    /// <summary>«Бали» tooltip header line (e.g. "Бали по КП:").</summary>
    string ScoreTooltipHeader,
    /// <summary>Per-control tooltip line template, "{0}" = code, "{1}" = points (e.g. "КП {0}: +{1}").</summary>
    string ScoreTooltipControl,
    /// <summary>Gross-points tooltip line template, "{0}" = gross (e.g. "Зібрано: {0}").</summary>
    string ScoreTooltipGross,
    /// <summary>Penalty tooltip line template, "{0}" = penalty (e.g. "Штраф: −{0}").</summary>
    string ScoreTooltipPenalty,
    /// <summary>Bonus tooltip line template, "{0}" = signed bonus (e.g. "Бонус: {0}").</summary>
    string ScoreTooltipBonus,
    /// <summary>Total tooltip line template, "{0}" = net total (e.g. "Сума: {0}").</summary>
    string ScoreTooltipTotal,
    /// <summary>Set-course cell tooltip: loss to the leader by overall time, "{0}" = gap (e.g. "Відставання: +{0}").</summary>
    string SplitLossTotal,
    /// <summary>Set-course cell tooltip: loss on this leg, "{0}" = gap (e.g. "На перегоні: +{0}").</summary>
    string SplitLossLeg);
