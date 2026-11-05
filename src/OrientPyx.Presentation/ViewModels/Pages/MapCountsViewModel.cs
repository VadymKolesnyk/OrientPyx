using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Кількість карт»: how many participants run in each group on each day — the numbers the map printer needs.
/// One table over the whole competition (a column per day, a row per group, a trailing «без групи» row when
/// some day has members with no group), a column total per day in the status bar, and an optional reserve
/// (a percentage and/or a minimum number of spare maps per group and day) shown next to the raw count. The
/// table exports to CSV / Excel through the participants' export flow. Read-only; never touches the session day.
/// </summary>
public sealed partial class MapCountsViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IParticipantExportFlow _exportFlow;

    private MapCountsData _data = MapCountsData.Empty;

    public MapCountsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IParticipantExportFlow exportFlow,
        IExportFileSaver fileSaver,
        ITableLayoutStore layoutStore,
        IDataChangeBus bus,
        IWorkspaceMode mode)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        _exportFlow = exportFlow;
        FileSaver = fileSaver;
        LayoutStore = layoutStore;

        // Singleton VM: reload on a competition change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Days, DataArea.Groups, DataArea.GroupDaySettings, DataArea.Participants, DataArea.ParticipantDays);
    }

    /// <summary>Writes the export to the file the user picked (handles a target locked by Excel). Used by the view.</summary>
    public IExportFileSaver FileSaver { get; }

    /// <summary>Persists the table's column layout (per competition, app default). Bound by the view.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.MapCounts";
    public override string TitleKey => "Page.MapCounts.Title";
    public override string TextKey => "Page.MapCounts.Text";

    // Lucide "map".
    public override string IconData =>
        "M14.1 6.4 9.9 4.3 M9 2.9v15.2 M15 5.9v15.2 M3.6 5.2 9 2.9l6 3 5.1-2.2a1 1 0 0 1 1.4.9v13.6a1 1 0 0 1-.6.9L15 21l-6-3-5.4 2.3A1 1 0 0 1 2.2 19.4V6.1a1 1 0 0 1 .6-.9Z";

    /// <summary>The table rows, one per group (plus the «без групи» row), with a count per day.</summary>
    public ObservableCollection<MapCountRowViewModel> Rows { get; } = [];

    /// <summary>The day numbers, in column order — the view builds one column (or two, with a reserve) per entry.</summary>
    [ObservableProperty]
    private IReadOnlyList<int> _dayNumbers = [];

    /// <summary>Spare maps as a percentage of the group's count (rounded up); 0 = none.</summary>
    [ObservableProperty]
    private int _reservePercent;

    /// <summary>At least this many spare maps per group and day (applied where the group runs); 0 = none.</summary>
    [ObservableProperty]
    private int _reserveMin;

    /// <summary>True when any reserve is set — the view then adds the «з запасом» columns.</summary>
    public bool ShowReserve => ReservePercent > 0 || ReserveMin > 0;

    // Custom +/- steppers, not NumericUpDown (its Windows automation peer crashes on spin). The percentage
    // moves in fives — map reserves are round figures; the minimum moves by one.
    [RelayCommand] private void IncrementReservePercent() => ReservePercent += 5;
    [RelayCommand] private void DecrementReservePercent() => ReservePercent = Math.Max(0, ReservePercent - 5);
    [RelayCommand] private void IncrementReserveMin() => ReserveMin++;
    [RelayCommand] private void DecrementReserveMin() => ReserveMin = Math.Max(0, ReserveMin - 1);

    /// <inheritdoc />
    protected override Task ReloadAsync(CancellationToken cancellationToken = default) => LoadAsync();

    public async Task LoadAsync()
    {
        _data = await _busy.RunAsync(() => _editor.GetMapCountsAsync());
        DayNumbers = _data.Days.Select(d => d.Number).ToList();
        RebuildRows();
    }

    // Rows are rebuilt (not updated in place) whenever the data or the reserve changes: the table is small and
    // the reserve columns read their values off the row, so a fresh set is the simplest way to keep them right.
    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var row in _data.Rows)
        {
            var name = row.GroupId is null ? Localization.Get("MapCounts.NoGroup") : row.GroupName;
            var tooltip = row.GroupId is null ? Localization.Get("MapCounts.NoGroup.Tooltip") : null;
            Rows.Add(new MapCountRowViewModel(name, tooltip, row.Counts, ReservePercent, ReserveMin));
        }
    }

    partial void OnReservePercentChanged(int value)
    {
        if (value < 0)
        {
            ReservePercent = 0;
            return;
        }
        OnPropertyChanged(nameof(ShowReserve));
        RebuildRows();
    }

    partial void OnReserveMinChanged(int value)
    {
        if (value < 0)
        {
            ReserveMin = 0;
            return;
        }
        OnPropertyChanged(nameof(ShowReserve));
        RebuildRows();
    }

    /// <summary>
    /// Exports the table's current on-screen view (captured by the view from the SheetTable) to CSV / Excel:
    /// shows the format modal and serialises the snapshot. Null when there is nothing to export or the user
    /// cancelled; the view runs the save dialog.
    /// </summary>
    public Task<ParticipantExportResult?> ExportAsync(CsvParticipantData view) =>
        _exportFlow.RunAsync(view, day: null, nameKey: "MapCounts.NamePart");
}

/// <summary>One table row: the group and its count per day, with the count-plus-reserve beside it.</summary>
public sealed class MapCountRowViewModel
{
    public MapCountRowViewModel(string groupName, string? noGroupTooltip, IReadOnlyList<int?> counts, int reservePercent, int reserveMin)
    {
        GroupName = groupName;
        NoGroupTooltip = noGroupTooltip;
        Counts = counts;
        WithReserve = counts.Select(c => c is { } n ? n + Reserve(n, reservePercent, reserveMin) : (int?)null).ToList();
        Total = counts.Sum(c => c ?? 0);
        TotalWithReserve = WithReserve.Sum(c => c ?? 0);
    }

    public string GroupName { get; }

    /// <summary>The synthetic «без групи» row — tinted so it is noticed before the maps go to print.</summary>
    public bool IsNoGroup => NoGroupTooltip is not null;

    /// <summary>The tint's tooltip on the «без групи» row; null on a real group's row.</summary>
    public string? NoGroupTooltip { get; }

    /// <summary>Participants per day (aligned to the page's day list); null where the group is not on the day.</summary>
    public IReadOnlyList<int?> Counts { get; }

    /// <summary>Participants plus the reserve per day; null where the group is not on the day.</summary>
    public IReadOnlyList<int?> WithReserve { get; }

    public int Total { get; }
    public int TotalWithReserve { get; }

    // The spare maps for a day the group runs on: the percentage share rounded up, but never below the minimum.
    // A day the group is on with nobody entered still gets the minimum — a late entry needs a map too.
    private static int Reserve(int count, int percent, int min)
    {
        var byPercent = (int)Math.Ceiling(count * percent / 100.0);
        return Math.Max(byPercent, min);
    }
}
