using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A live preview of the online spectator results table for the active day — a light-theme mock-up that mirrors
/// what the React frontend renders from the published data (centred title/subtitle, one section per group with a
/// results table). Built from the day's <see cref="OnlineResultsSnapshot"/> (the exact data the publisher sends),
/// so what the user configures matches what spectators see. Each column carries its large/small-screen
/// visibility so the table can mark columns hidden on the phone; the header cells drive the drag-reorder
/// (see <see cref="OnlinePreviewTable"/>).
/// </summary>
public sealed partial class OnlinePreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>True when there are no rows to show (placeholder hint instead of a table).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>The columns shown on the large screen, in on-screen order. Header cells drive the drag-reorder.</summary>
    public ObservableCollection<OnlinePreviewColumn> Columns { get; } = [];

    /// <summary>The group sections, each a caption + a results table (mirrors the frontend layout).</summary>
    public ObservableCollection<OnlinePreviewSection> Sections { get; } = [];

    /// <summary>Raised ONCE after both <see cref="Columns"/> and <see cref="Sections"/> have been fully
    /// repopulated, so the (expensive) table control rebuilds a single time per refresh. The view subscribes to
    /// this instead of the collections' CollectionChanged.</summary>
    public event EventHandler? Changed;

    /// <summary>Raises <see cref="Changed"/> — call after finishing a batch update of the collections.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>One visible column in the online preview: its stable key (<see cref="ResultColumnDef.Key"/>, the drag
/// payload), its header text, the result-column kind (drives left/centre alignment), and whether it is also shown
/// on the small (phone) screen — a column with <see cref="ShownOnSmall"/> = false gets a "phone-hidden" badge so
/// the effect of the toggle is visible in the preview.</summary>
public sealed record OnlinePreviewColumn(string Key, string Header, ResultColumn Column, bool ShownOnSmall);

/// <summary>One group section in the preview: the caption line and the rows.</summary>
public sealed record OnlinePreviewSection(string Name, string Caption, IReadOnlyList<OnlinePreviewRow> Rows);

/// <summary>One result row: the cell strings (parallel to <see cref="OnlinePreviewViewModel.Columns"/>) and
/// whether the runner is unplaced (DNS/MP/…) so the row can be greyed like the frontend output.</summary>
public sealed record OnlinePreviewRow(IReadOnlyList<string> Values, bool Unplaced);
