namespace OrientPyx.BusinessLogic.Models;

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

    /// <summary>Competition-name line (centred), printed above the title. Blank ⇒ nothing printed.</summary>
    public string CompetitionName { get; init; } = string.Empty;

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

    /// <summary>Short (abbreviated) form of each column caption, parallel to <see cref="ColumnHeaders"/> — e.g.
    /// "Дата нар." for "Дата народження", "Рез." for "Результат". The renderer falls back to the short form for
    /// a column too narrow to fit the full caption on one line. Empty entry ⇒ no abbreviation (use the full
    /// caption). May be shorter than <see cref="ColumnHeaders"/> or empty; the renderer guards the index.</summary>
    public IReadOnlyList<string> ColumnHeadersShort { get; init; } = [];

    /// <summary>Per-column flag (parallel to <see cref="ColumnHeaders"/>): <c>true</c> = the column's body text
    /// may wrap onto several lines (free-text columns — name, club, coach…), so its width is sized to the
    /// TYPICAL content and a long outlier wraps rather than widening the whole column; <c>false</c> = the data
    /// is atomic and must stay on one line (рік, № з/п, номер, результат, місце, кваліфікація — short codes),
    /// so the column is always wide enough for its longest value. Missing entry ⇒ treated as non-wrapping.</summary>
    public IReadOnlyList<bool> ColumnBodyWrap { get; init; } = [];

    /// <summary>Per-column shrink priority (parallel to <see cref="ColumnHeaders"/>) used when the table is too
    /// wide for the page: it decides which columns give up width first and how far they may shrink.
    /// <list type="bullet">
    /// <item><b>1</b> — never narrowed: kept at its full floor (data + full header), the first thing protected.</item>
    /// <item><b>2 / 3 / 4</b> — may give up width, increasingly willingly (4 yields first and furthest), but never
    /// below a sensible floor derived from the typical content length — so they shrink fairly rather than collapsing
    /// to nothing.</item>
    /// </list>
    /// Missing entry ⇒ treated as the most-protected priority (1). See the renderers' DistributeWidths.</summary>
    public IReadOnlyList<int> ColumnShrinkPriority { get; init; } = [];

    /// <summary>The group sections, in display order.</summary>
    public IReadOnlyList<ResultProtocolSection> Sections { get; init; } = [];

    /// <summary>The officials' signature block printed at the very end (chief judge, secretary, jury). Empty
    /// when none are configured. Shared across all protocol kinds (results + both start protocols).</summary>
    public IReadOnlyList<ProtocolOfficial> Officials { get; init; } = [];

    /// <summary>The page footer (нижній колонтитул) printed at the bottom of every page: the software name,
    /// the generation time, and the page number. Carries only the localized label parts; the renderer adds the
    /// live page-number field and stamps the actual generation timestamp. <c>null</c> ⇒ no footer is printed.
    /// Shared across all protocol kinds (results + both start protocols).</summary>
    public ProtocolFooter? Footer { get; init; }
}

/// <summary>
/// The localized pieces of a protocol's page footer (нижній колонтитул). The renderer lays them out across the
/// bottom of every page — software name left, generation time centred, page number right — supplying the live
/// page-number field and the actual generation timestamp itself (so the document stays layout-only and these
/// stay localization-free). Built in BusinessLogic from <c>ILocalizationService</c> values.
/// </summary>
/// <param name="SoftwareName">The program name shown in the footer ("П/З: OrientPyx"). Blank ⇒ omitted.</param>
/// <param name="GeneratedLabel">Caption before the generation timestamp ("Згенеровано"). Blank ⇒ the timestamp
/// is printed alone.</param>
/// <param name="PageLabel">Caption before the page number ("Сторінка"). Blank ⇒ the number is printed alone.</param>
public sealed record ProtocolFooter(string SoftwareName, string GeneratedLabel, string PageLabel);

/// <summary>
/// One official line in a protocol's trailing signature block: a localized role caption ("Головний суддя"),
/// the person's name, and an optional judge category (суддівська категорія). Pre-formatted in the builder so
/// the renderers stay layout-only and localization-free.
/// </summary>
public sealed record ProtocolOfficial(string Role, string Name, string Category)
{
    /// <summary>The name with its category appended in parentheses when present ("Доценко О. (НК)").</summary>
    public string NameWithCategory => Category.Length > 0 ? $"{Name} ({Category})" : Name;
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

    /// <summary>When <c>true</c> the caption is rendered as a distinct full-width <b>shaded band</b> (a single
    /// centred row spanning every column), not a plain bold line. Used by the judges' start protocol so each
    /// start-minute caption ("13:01:00") stands out as a band above its runners — matching the classic printed
    /// sheet. Regular group/result sections leave this <c>false</c> (plain bold caption).</summary>
    public bool IsBanded { get; init; }

    /// <summary>Course length text ("1.300 км"), blank when unknown — part of the section sub-caption.</summary>
    public string DistanceText { get; init; } = string.Empty;

    /// <summary>Number of control points text ("12 КП"), blank when unknown — part of the section sub-caption.</summary>
    public string ControlCountText { get; init; } = string.Empty;

    /// <summary>Time-limit text ("Контрольний час: 24:00:00"), blank when none — part of the section sub-caption.</summary>
    public string TimeLimitText { get; init; } = string.Empty;

    /// <summary>Course-setter text ("Начальник дистанції: Рачук Тарас"), blank when none — printed next to the
    /// group caption. The group's per-day override wins over the competition default (see the builder).</summary>
    public string CourseSetterText { get; init; } = string.Empty;

    /// <summary>The rank-award calculation line ("Клас дистанції: КМС ; Ранг змагань: 790 балів ; КМСУ 120%
    /// 00:24:08 ; …"), printed under the table to show how the «виконаний розряд» values were derived. Blank
    /// when the group awards no rank or the awarded-rank column is hidden (see the builder). Pre-formatted, so
    /// the renderers stay layout-only.</summary>
    public string RankCalculationText { get; init; } = string.Empty;

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
