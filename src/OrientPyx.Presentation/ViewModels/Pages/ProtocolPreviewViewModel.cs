using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A live preview of the protocol document the user is configuring: the page header (title / subtitle /
/// type-date-venue row) plus the group sections rendered as real, print-faithful tables — the same multi-
/// section layout, font and density the .docx export produces, so what the user sees is what they get. Built
/// from the same builder output the export uses. Rebuilt whenever a column is reordered, hidden/shown, the
/// header text changes, or the previewed day changes. The column headers (repeated per section) carry the
/// drag-reorder interaction (see <see cref="ProtocolPreviewColumn"/>); the body rows are read-only.
/// </summary>
public sealed partial class ProtocolPreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private string _competitionName = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    [ObservableProperty]
    private string _competitionType = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private bool _isLandscape;

    /// <summary>True when there are no participant rows to show (placeholder hint instead of a table).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>A single line summarising the applied filters, shown just under the header (participant
    /// statement only). Blank for the protocols.</summary>
    [ObservableProperty]
    private string _filterSummary = string.Empty;

    /// <summary>True when <see cref="FilterSummary"/> is non-blank (drives the line's visibility on the page).</summary>
    [ObservableProperty]
    private bool _hasFilterSummary;

    /// <summary>The visible columns, in on-page order. Header cells (repeated per section) drive the drag-reorder.</summary>
    public ObservableCollection<ProtocolPreviewColumn> Columns { get; } = [];

    /// <summary>The group sections, in display order — each rendered as its own caption + table (like the .docx).</summary>
    public ObservableCollection<ProtocolPreviewSection> Sections { get; } = [];

    /// <summary>The officials' signature lines printed below the table (chief judge / secretary / jury), each
    /// "<role>: <name (category)>". Empty when none are configured.</summary>
    public ObservableCollection<string> Officials { get; } = [];

    /// <summary>True when there are officials to print (drives the block's visibility on the page).</summary>
    [ObservableProperty]
    private bool _hasOfficials;
}

/// <summary>
/// One visible column in the preview table: its localized caption plus the underlying
/// an opaque string <see cref="Key"/> (the owning view-model's column enum name), so a header drag maps
/// back to the configurable column list to reorder it without coupling the preview to a specific enum
/// (the results and start protocols share this preview).
/// </summary>
public sealed class ProtocolPreviewColumn
{
    public ProtocolPreviewColumn(string key, string caption, string shortCaption = "", bool bodyWraps = false,
        int shrinkPriority = 1)
    {
        Key = key;
        Caption = caption;
        ShortCaption = shortCaption;
        BodyWraps = bodyWraps;
        ShrinkPriority = shrinkPriority;
    }

    /// <summary>Stable identity of the column (its enum name) — the drag payload, resolved by the owning VM.</summary>
    public string Key { get; }

    public string Caption { get; }

    /// <summary>Abbreviated caption ("Дата нар.", "Рез."), or empty when the column has none. The preview table
    /// falls back to this when the column is too narrow to fit <see cref="Caption"/> on one line.</summary>
    public string ShortCaption { get; }

    /// <summary>True when the column's body text may wrap (free-text columns — name, club, coach…), so the table
    /// sizes it to the typical content and long values wrap; false for short-code columns that stay on one line.</summary>
    public bool BodyWraps { get; }

    /// <summary>How willingly the column gives up width when the table overflows the page (1 = never narrowed;
    /// 4 = yields first and furthest). Mirrors <c>ResultProtocolDocument.ColumnShrinkPriority</c> so the preview
    /// squeezes the same columns the .docx export does.</summary>
    public int ShrinkPriority { get; }
}

/// <summary>One group section in the preview: a bold group caption, an optional course sub-caption, and the
/// ordered body rows. Mirrors a <c>ResultProtocolSection</c>/start section so the preview stacks groups on the
/// page exactly as the .docx does.</summary>
public sealed class ProtocolPreviewSection
{
    public ProtocolPreviewSection(string groupName, string subcaption, IReadOnlyList<ProtocolPreviewRow> rows,
        string courseSetter = "", bool isBanded = false, string rankCalculation = "")
    {
        GroupName = groupName;
        Subcaption = subcaption;
        Rows = rows;
        CourseSetter = courseSetter;
        IsBanded = isBanded;
        RankCalculation = rankCalculation;
        HasRankCalculation = rankCalculation.Length > 0;
    }

    public string GroupName { get; }

    /// <summary>True ⇒ render the caption as a shaded full-width band (judges' start protocol minute bands), with
    /// the column header printed once at the top of the table instead of repeated per section.</summary>
    public bool IsBanded { get; }

    /// <summary>Course facts line ("Довжина: 1.300 км · 12 КП"), blank for start sections / unknown courses.</summary>
    public string Subcaption { get; }

    /// <summary>"Начальник дистанції: …" printed on the caption line; blank when no course-setter.</summary>
    public string CourseSetter { get; }

    /// <summary>The rank-derivation line printed under the table ("Клас дистанції: КМС ; Ранг змагань: 790 балів
    /// ; …"), blank when the group awards no rank or its column is hidden.</summary>
    public string RankCalculation { get; }

    /// <summary>True when <see cref="RankCalculation"/> is non-blank (drives the line's visibility).</summary>
    public bool HasRankCalculation { get; }

    public IReadOnlyList<ProtocolPreviewRow> Rows { get; }
}

/// <summary>One body row in a preview section: the formatted cell strings and whether it is a team caption
/// row (rendered bold for a rogaine section), aligned to <see cref="ProtocolPreviewViewModel.Columns"/>.</summary>
public sealed class ProtocolPreviewRow
{
    public ProtocolPreviewRow(IReadOnlyList<string> cells, bool isTeamHeader,
        IReadOnlyList<bool>? boldCells = null)
    {
        Cells = cells;
        IsTeamHeader = isTeamHeader;
        BoldCells = boldCells;
    }

    public IReadOnlyList<string> Cells { get; }

    public bool IsTeamHeader { get; }

    /// <summary>Optional per-cell bold mask parallel to <see cref="Cells"/>: a <c>true</c> at an index ⇒ that
    /// cell is drawn bold (the statement's own-chip cells). Null ⇒ no per-cell bolding.</summary>
    public IReadOnlyList<bool>? BoldCells { get; }
}
