namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Raw split data for one day, gathered by the editor service from the computed results and the
/// discipline-built splits: every group that runs on the day, each with its course metadata, layout and
/// its member rows (already carrying the per-runner <see cref="SplitsView"/> and result). The layer-neutral
/// <c>ISplitExportBuilder</c> turns this — plus the header settings + localized labels — into a renderable
/// <see cref="SplitExportDocument"/>. Mirrors <see cref="ResultProtocolData"/>.
/// </summary>
public sealed record SplitExportData(IReadOnlyList<SplitExportDataGroup> Groups);

/// <summary>One group's raw split data: name, course metadata, layout, prescribed controls and member rows.</summary>
public sealed record SplitExportDataGroup(
    string Name,
    int Order,
    SplitsLayout Layout,
    decimal? DistanceKm,
    int? ControlCount,
    bool HasPoints,
    /// <summary>Set-course prescribed running controls in order (table columns); empty for scored.</summary>
    IReadOnlyList<string> Controls,
    IReadOnlyList<SplitExportDataRow> Rows);

/// <summary>
/// One participant's raw split row: identity, computed result and the discipline-built splits. The builder
/// formats the result into the display columns and orders the rows (placed finishers first by place/score,
/// then the rest).
/// </summary>
public sealed record SplitExportDataRow(
    string Number,
    string FullName,
    string Team,
    ParticipantDayResult Result,
    SplitsView Splits);
