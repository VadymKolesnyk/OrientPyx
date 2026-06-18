namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// A ready-to-render results protocol: a page header (titles + venue/date) followed by one section per
/// group. Built in the layer-neutral BusinessLogic layer from the computed day results; the DataAccess
/// writer renders it to a Word (.docx) document. All values are pre-formatted strings — the writer only
/// lays them out — and the column captions are supplied here too, so the writer stays localization-free.
/// </summary>
public sealed class ResultProtocolDocument
{
    /// <summary>Page orientation chosen in the settings.</summary>
    public ProtocolOrientation Orientation { get; init; } = ProtocolOrientation.Portrait;

    /// <summary>Main title line (centred, bold).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Sub-title / organisation line under the title (centred).</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Venue ("м. Київ") — printed right-aligned on the date/type row.</summary>
    public string Venue { get; init; } = string.Empty;

    /// <summary>Date ("31.05.2026") — printed left-aligned on the date/type row.</summary>
    public string DateText { get; init; } = string.Empty;

    /// <summary>Competition type — printed centred on the date/venue row. Blank ⇒ nothing in the centre.</summary>
    public string CompetitionType { get; init; } = string.Empty;

    /// <summary>The column captions, in on-page order — one per <see cref="ResultProtocolSection.Rows"/> cell.</summary>
    public IReadOnlyList<string> ColumnHeaders { get; init; } = [];

    /// <summary>The group sections, in display order.</summary>
    public IReadOnlyList<ResultProtocolSection> Sections { get; init; } = [];
}

/// <summary>
/// One group's block in the protocol: a group caption line (name + course info) and the participant rows,
/// already ordered (placed finishers first by place, then the rest) and pre-formatted. Each row's cells
/// align with <see cref="ResultProtocolDocument.ColumnHeaders"/>.
/// </summary>
public sealed class ResultProtocolSection
{
    /// <summary>Group name (the "Вікова група KIDS" caption).</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>Course length text ("1.300 км"), blank when unknown — part of the section sub-caption.</summary>
    public string DistanceText { get; init; } = string.Empty;

    /// <summary>Number of control points text ("12 КП"), blank when unknown — part of the section sub-caption.</summary>
    public string ControlCountText { get; init; } = string.Empty;

    /// <summary>Time-limit text ("Контрольний час: 24:00:00"), blank when none — part of the section sub-caption.</summary>
    public string TimeLimitText { get; init; } = string.Empty;

    /// <summary>The data rows; each is the ordered cell strings matching the column headers.</summary>
    public IReadOnlyList<ResultProtocolBodyRow> Rows { get; init; } = [];
}

/// <summary>
/// One body row in a section: either a normal participant row or — for a teamed (rogaine) section — a team
/// caption row that introduces a team's members. A team row carries the team name plus its cell values (the
/// team place/score land in the matching columns); the renderer draws it bold/spanning so the members below
/// read as one team. <see cref="IsTeamHeader"/> distinguishes the two.
/// </summary>
public sealed record ResultProtocolBodyRow(
    IReadOnlyList<string> Cells,
    bool IsTeamHeader = false,
    string TeamName = "");
