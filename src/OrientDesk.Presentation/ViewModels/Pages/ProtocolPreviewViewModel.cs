using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A live preview of the results-protocol document the user is configuring: the page header (title /
/// subtitle / type-date-venue row) plus one group section rendered as a real table. It is built from the
/// same <see cref="OrientDesk.BusinessLogic.Interfaces.IResultProtocolBuilder"/> output the .docx export
/// uses, so what the user sees is what they get. Rebuilt whenever a column is reordered, hidden/shown, the
/// header text changes, or the previewed day changes. The column headers carry the drag-reorder interaction
/// (see <see cref="ProtocolPreviewColumn"/>); the body rows are read-only formatted cells.
/// </summary>
public sealed partial class ProtocolPreviewViewModel : ObservableObject
{
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

    /// <summary>The previewed group's caption ("Вікова група KIDS"), blank when there is no group.</summary>
    [ObservableProperty]
    private string _groupName = string.Empty;

    /// <summary>The previewed group's course sub-caption ("Довжина: 1.300 км · 12 КП · Контрольний час: 24:00:00").</summary>
    [ObservableProperty]
    private string _groupSubcaption = string.Empty;

    /// <summary>The visible columns, in on-page order. Header cells drive the drag-reorder.</summary>
    public ObservableCollection<ProtocolPreviewColumn> Columns { get; } = [];

    /// <summary>The previewed body rows (capped), each a list of formatted cells aligned to <see cref="Columns"/>.</summary>
    public ObservableCollection<ProtocolPreviewRow> Rows { get; } = [];
}

/// <summary>
/// One visible column in the preview table: its localized caption plus the underlying
/// an opaque string <see cref="Key"/> (the owning view-model's column enum name), so a header drag maps
/// back to the configurable column list to reorder it without coupling the preview to a specific enum
/// (the results and start protocols share this preview).
/// </summary>
public sealed class ProtocolPreviewColumn
{
    public ProtocolPreviewColumn(string key, string caption)
    {
        Key = key;
        Caption = caption;
    }

    /// <summary>Stable identity of the column (its enum name) — the drag payload, resolved by the owning VM.</summary>
    public string Key { get; }

    public string Caption { get; }
}

/// <summary>One body row in the preview table: the formatted cell strings and whether it is a team caption
/// row (rendered bold for a rogaine section), aligned to <see cref="ProtocolPreviewViewModel.Columns"/>.</summary>
public sealed class ProtocolPreviewRow
{
    public ProtocolPreviewRow(IReadOnlyList<string> cells, bool isTeamHeader)
    {
        Cells = cells;
        IsTeamHeader = isTeamHeader;
    }

    public IReadOnlyList<string> Cells { get; }

    public bool IsTeamHeader { get; }
}
