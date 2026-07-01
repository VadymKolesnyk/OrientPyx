using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Shared;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One output file in the results monitor («Результати на монітор»): its target .html path and page title,
/// which of the day's groups it shows, its column layout (a shared <see cref="ResultColumnsEditorViewModel"/>)
/// and its auto-refresh / auto-scroll timing. Raises <see cref="Changed"/> on any edit so the host marks the
/// settings dirty. <see cref="ToModel"/> snapshots it back to a <see cref="MonitorFile"/> for saving.
///
/// Drives a live <see cref="MonitorPreviewViewModel"/> that mirrors the actual monitor HTML screen the file
/// produces (centred header + blue group captions + result tables): its column headers are drag-reorderable
/// (via <see cref="MoveColumnByKey"/>) and the show/hide check-boxes below toggle visibility — like the
/// «Протокол результатів» configuration UI, but rendered as the monitor's own output.
/// </summary>
public sealed partial class MonitorFileViewModel : ObservableObject
{
    private readonly ObservableCollection<string> _availableGroups;
    private readonly ILocalizationService _localization;
    private bool _suppressChange;

    private readonly ResultColumnsEditorViewModel _sharedColumns;

    public MonitorFileViewModel(
        ILocalizationService localization, MonitorFile model, ObservableCollection<string> availableGroups,
        ResultColumnsEditorViewModel sharedColumns)
    {
        _availableGroups = availableGroups;
        _localization = localization;
        _sharedColumns = sharedColumns;
        // Re-raise the category toggle-button labels when the UI language changes.
        _localization.PropertyChanged += (_, _) => RaiseToggleLabels();
        // Files are addressed by name only now; an older saved value may be a full path — keep just the leaf.
        _path = string.IsNullOrWhiteSpace(model.Path) ? string.Empty : System.IO.Path.GetFileName(model.Path);
        _title = model.Title;
        _refreshSeconds = Math.Max(MonitorFile.MinRefreshSeconds, model.RefreshSeconds);
        _scrollSpeed = Math.Max(0, model.ScrollSpeed);
        _enabled = model.Enabled;

        var selected = model.GroupNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        BuildGroupToggles(selected);

        // Keep the group toggle list in sync if the day's groups change while the page is open.
        _availableGroups.CollectionChanged += OnAvailableGroupsChanged;
    }

    /// <summary>Raised on any user edit, so the host VM can mark its settings unsaved.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when this file's preview needs rebuilding (its groups changed). The host VM builds the
    /// monitor document for this file and feeds it back via <see cref="ApplyPreview"/>.</summary>
    public event EventHandler? PreviewRefreshRequested;

    /// <summary>The live monitor-screen preview (mirrors the generated HTML). Header drags reorder the SHARED
    /// columns via <see cref="MoveColumnByKey"/>; the body shows the day's real computed rows.</summary>
    public MonitorPreviewViewModel Preview { get; } = new();

    /// <summary>One toggle per available group; checked = shown in this file. Empty selection = all groups.</summary>
    public ObservableCollection<GroupToggle> Groups { get; } = [];

    /// <summary>Groups whose name starts with «Ж» or «W» (women) — first toggle column.</summary>
    public ObservableCollection<GroupToggle> WomenGroups { get; } = [];

    /// <summary>Groups whose name starts with «Ч», «М» or «M» (men) — second toggle column.</summary>
    public ObservableCollection<GroupToggle> MenGroups { get; } = [];

    /// <summary>All other groups — third toggle column.</summary>
    public ObservableCollection<GroupToggle> OtherGroups { get; } = [];

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private int _refreshSeconds;

    [ObservableProperty]
    private int _scrollSpeed;

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Short display label for the file list (the file name, or a placeholder when no path yet).</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Path) ? System.IO.Path.GetFileName(Path) :
        !string.IsNullOrWhiteSpace(Title) ? Title : "—";

    partial void OnPathChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        RaiseChanged();
    }

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        RaiseChanged();
    }

    partial void OnRefreshSecondsChanged(int value) => RaiseChanged();
    partial void OnScrollSpeedChanged(int value) => RaiseChanged();
    partial void OnEnabledChanged(bool value) => RaiseChanged();

    [RelayCommand]
    private void IncrementRefresh() => RefreshSeconds++;

    [RelayCommand]
    private void DecrementRefresh()
    {
        if (RefreshSeconds > MonitorFile.MinRefreshSeconds)
            RefreshSeconds--;
    }

    [RelayCommand]
    private void IncrementScroll() => ScrollSpeed += 5;

    [RelayCommand]
    private void DecrementScroll()
    {
        if (ScrollSpeed >= 5)
            ScrollSpeed -= 5;
        else
            ScrollSpeed = 0;
    }

    /// <summary>Snapshots the current edits back to a persistable model.</summary>
    public MonitorFile ToModel()
    {
        // An all-checked or all-unchecked selection both mean "everything" — store it as empty so the file
        // keeps following the day's group set rather than freezing today's names.
        var checkedNames = Groups.Where(g => g.IsSelected).Select(g => g.Name).ToList();
        var groupNames = checkedNames.Count == 0 || checkedNames.Count == Groups.Count
            ? new List<string>()
            : checkedNames;

        return new MonitorFile(
            Path: (Path ?? string.Empty).Trim(),
            Title: (Title ?? string.Empty).Trim(),
            GroupNames: groupNames,
            Columns: null, // columns are shared across files now (stored on MonitorSettings)
            RefreshSeconds: Math.Max(MonitorFile.MinRefreshSeconds, RefreshSeconds),
            ScrollSpeed: Math.Max(0, ScrollSpeed),
            Enabled: Enabled);
    }

    private void BuildGroupToggles(HashSet<string> selected)
    {
        _suppressChange = true;
        Groups.Clear();
        WomenGroups.Clear();
        MenGroups.Clear();
        OtherGroups.Clear();
        foreach (var name in _availableGroups)
        {
            // No stored selection (empty) means "all" — show every group ticked.
            var isOn = selected.Count == 0 || selected.Contains(name);
            var toggle = new GroupToggle(name) { IsSelected = isOn };
            toggle.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GroupToggle.IsSelected))
                {
                    RaiseChanged();
                    RequestPreviewRefresh();
                    RaiseToggleLabels();
                }
            };
            Groups.Add(toggle);
            CategoryOf(name).Add(toggle);
        }
        _suppressChange = false;
        RaiseToggleLabels();
    }

    // The three toggle columns: women (Ж/W), men (Ч/М/M), then everything else. The first letter decides;
    // both Cyrillic «М» and Latin «M» count as men.
    private ObservableCollection<GroupToggle> CategoryOf(string name)
    {
        var c = string.IsNullOrEmpty(name) ? '\0' : char.ToUpperInvariant(name[0]);
        return c switch
        {
            'Ж' or 'W' => WomenGroups,
            'Ч' or 'М' or 'M' => MenGroups,
            _ => OtherGroups,
        };
    }

    // Each category button toggles ALL its groups: if every group is already on, the click clears them; else it
    // selects them all. The label flips between «Зняти всі» and «Позначити всі» accordingly.
    public bool WomenAllSelected => AllSelected(WomenGroups);
    public bool MenAllSelected => AllSelected(MenGroups);
    public bool OtherAllSelected => AllSelected(OtherGroups);

    public string WomenToggleLabel => ToggleLabel(WomenAllSelected);
    public string MenToggleLabel => ToggleLabel(MenAllSelected);
    public string OtherToggleLabel => ToggleLabel(OtherAllSelected);

    private string ToggleLabel(bool allSelected) =>
        _localization.Get(allSelected ? "Monitor.File.Groups.DeselectAll" : "Monitor.File.Groups.SelectAll");

    private static bool AllSelected(ObservableCollection<GroupToggle> toggles) =>
        toggles.Count > 0 && toggles.All(t => t.IsSelected);

    [RelayCommand]
    private void ToggleWomen() => ToggleAll(WomenGroups);

    [RelayCommand]
    private void ToggleMen() => ToggleAll(MenGroups);

    [RelayCommand]
    private void ToggleOther() => ToggleAll(OtherGroups);

    // Flips every group in the column at once. Suppresses the per-toggle Changed / preview-refresh (each would
    // otherwise mark dirty + rebuild the preview once PER group — the source of the lag), then fires a single
    // dirty + preview refresh + label update after the whole batch.
    private void ToggleAll(ObservableCollection<GroupToggle> toggles)
    {
        var turnOn = !AllSelected(toggles);
        _suppressChange = true;
        foreach (var t in toggles)
            t.IsSelected = turnOn;
        _suppressChange = false;

        RaiseChanged();
        RequestPreviewRefresh();
        RaiseToggleLabels();
    }

    private void RaiseToggleLabels()
    {
        OnPropertyChanged(nameof(WomenAllSelected));
        OnPropertyChanged(nameof(MenAllSelected));
        OnPropertyChanged(nameof(OtherAllSelected));
        OnPropertyChanged(nameof(WomenToggleLabel));
        OnPropertyChanged(nameof(MenToggleLabel));
        OnPropertyChanged(nameof(OtherToggleLabel));
    }

    private void OnAvailableGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Preserve the current ticks across a refresh of the available-group list.
        var selected = Groups.Where(g => g.IsSelected).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BuildGroupToggles(selected);
        RequestPreviewRefresh();
    }

    private void RaiseChanged()
    {
        if (!_suppressChange)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RequestPreviewRefresh()
    {
        if (!_suppressChange)
            PreviewRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── Live preview (mirrors the generated monitor HTML) ────────────────────────────────────────────────

    /// <summary>
    /// Fills <see cref="Preview"/> from a freshly-built monitor document for this file (its visible columns +
    /// the day's group sections), so the preview shows exactly what the HTML screen will. Called by the host VM
    /// whenever the file is selected or a column/group changes. The preview's column <c>Key</c> is the stable
    /// <see cref="ResultColumnDef.Key"/>, so a header drag maps back to the editor's column list.
    /// </summary>
    // The preview is a MOCK-UP, not the full screen — cap the total body rows so the visual tree the table
    // control builds stays small and rebuilds fast (the real HTML shows everyone). A per-group cap keeps every
    // shown group represented rather than the first group eating the whole budget.
    private const int PreviewTotalRowCap = 10;
    private const int PreviewPerGroupCap = 10;

    public void ApplyPreview(MonitorDocument? document)
    {
        Preview.Columns.Clear();
        Preview.Sections.Clear();

        if (document is null)
        {
            Preview.Title = string.Empty;
            Preview.Subtitle = string.Empty;
            Preview.IsEmpty = true;
            Preview.RaiseChanged();
            return;
        }

        Preview.Title = document.Title;
        Preview.Subtitle = string.Equals(document.Subtitle, document.Title, StringComparison.Ordinal)
            ? string.Empty : document.Subtitle;

        foreach (var col in document.Columns)
        {
            var def = ResultColumnDef.All.FirstOrDefault(d => d.Column == col.Column);
            var key = def?.Key ?? col.Column.ToString();
            Preview.Columns.Add(new MonitorPreviewColumn(key, col.Header, col.Column));
        }

        var remaining = PreviewTotalRowCap;
        foreach (var g in document.Groups)
        {
            if (remaining <= 0)
                break;
            var take = Math.Min(Math.Min(PreviewPerGroupCap, remaining), g.Cells.Count);
            var rows = new List<MonitorPreviewRow>(take);
            for (var i = 0; i < take; i++)
                rows.Add(new MonitorPreviewRow(g.Cells[i].Values, g.Cells[i].Unplaced));
            remaining -= take;
            Preview.Sections.Add(new MonitorPreviewSection(g.Name, g.Caption, rows));
        }

        Preview.IsEmpty = Preview.Sections.Count == 0 || Preview.Sections.All(s => s.Rows.Count == 0);
        Preview.RaiseChanged(); // one rebuild after both collections are populated
    }

    /// <summary>Moves a column next to another in the SHARED column list (drag-reorder from the preview header).
    /// Keys are the stable <see cref="ResultColumnDef.Key"/>; resolved against ALL columns so hidden ones don't
    /// skew the destination. Reordering raises the shared editor's Changed, which re-renders every file's
    /// preview — the column order is shared across all files.</summary>
    public void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
        var dragged = _sharedColumns.Columns.FirstOrDefault(c => c.Key == draggedKey);
        var target = _sharedColumns.Columns.FirstOrDefault(c => c.Key == targetKey);
        _sharedColumns.MoveColumn(dragged, target, insertAfter);
    }
}

/// <summary>One group check-box within a monitor file: the group name and whether the file shows it.</summary>
public sealed partial class GroupToggle : ObservableObject
{
    public GroupToggle(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private bool _isSelected;
}
