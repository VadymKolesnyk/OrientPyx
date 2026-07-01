using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A live preview of the monitor HTML screen a file will produce — the same content
/// <c>HtmlMonitorWriter</c> renders (centred title/subtitle header, then one section per group with a blue
/// accent caption and a results table), so what the user configures matches what goes out to the venue
/// screen. Built from the day's computed <see cref="MonitorDocument"/>; the table headers (repeated per
/// section) carry the drag-reorder interaction (see <see cref="MonitorPreviewTable"/>).
/// </summary>
public sealed partial class MonitorPreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>True when there are no rows to show (placeholder hint instead of a table).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>The visible columns, in on-screen order. Header cells (per section) drive the drag-reorder.</summary>
    public ObservableCollection<MonitorPreviewColumn> Columns { get; } = [];

    /// <summary>The group sections, each a blue caption + a results table (mirrors the HTML layout).</summary>
    public ObservableCollection<MonitorPreviewSection> Sections { get; } = [];

    /// <summary>Raised ONCE after both <see cref="Columns"/> and <see cref="Sections"/> have been fully
    /// repopulated, so the (expensive) table control rebuilds a single time per refresh — not once per
    /// collection mutation. The view subscribes to this instead of the collections' CollectionChanged.</summary>
    public event EventHandler? Changed;

    /// <summary>Raises <see cref="Changed"/> — call after finishing a batch update of the collections.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>One visible column in the monitor preview: its stable key (<see cref="ResultColumnDef.Key"/>, the
/// drag payload), its header text, and the result-column kind (drives left/centre alignment, mirroring the
/// HTML <c>c-name</c>/<c>c-num</c>/<c>c-place</c> classes).</summary>
public sealed record MonitorPreviewColumn(string Key, string Header, ResultColumn Column);

/// <summary>One group section in the preview: the blue caption, an optional facts sub-caption, and the rows.</summary>
public sealed record MonitorPreviewSection(string Name, string Caption, IReadOnlyList<MonitorPreviewRow> Rows);

/// <summary>One result row: the cell strings (parallel to <see cref="MonitorPreviewViewModel.Columns"/>) and
/// whether the runner is unplaced (DNS/MP/…) so the row can be greyed like the HTML output.</summary>
public sealed record MonitorPreviewRow(IReadOnlyList<string> Values, bool Unplaced);
