using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Варіанти за номерами»: which variation (map) every number runs on the day — a relay leg number «101.2» or a
/// scatter runner's bib — as imported from OCAD or typed on «Команди», for handing out the maps. One read-only table
/// (group, number, variation, its controls, team, runner) over the session's day; a leg number of a «за номером етапу»
/// relay with no variation and a code the group has no variant for are tinted. Exports to CSV / Excel through the
/// participants' export flow.
/// </summary>
public sealed partial class NumberVariantsViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IParticipantExportFlow _exportFlow;
    private bool _syncingDay;

    public NumberVariantsViewModel(
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

        // Singleton VM: reload on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Days, DataArea.Groups, DataArea.GroupDaySettings, DataArea.Participants, DataArea.ParticipantDays,
            DataArea.Relay);
    }

    /// <summary>Writes the export to the file the user picked (handles a target locked by Excel). Used by the view.</summary>
    public IExportFileSaver FileSaver { get; }

    /// <summary>Persists the table's column layout (per competition, app default). Bound by the view.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.NumberVariants";
    public override string TitleKey => "Page.NumberVariants.Title";
    public override string TextKey => "Page.NumberVariants.Text";

    // Lucide "list-ordered".
    public override string IconData =>
        "M10 12h11 M10 18h11 M10 6h11 M4 10h2 M4 6h1v4 M6 18H4c0-1 2-2 2-3s-1-1.5-2-1";

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    /// <summary>The table rows, one per number and leg.</summary>
    public ObservableCollection<NumberVariantRowViewModel> Rows { get; } = [];

    /// <summary>True when the day has no number with a variation (the page then explains where they come from).</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <inheritdoc />
    protected override Task ReloadAsync(CancellationToken cancellationToken = default) => LoadAsync();

    public async Task LoadAsync()
    {
        var hasDay = _session.CurrentDay is not null;
        var (days, data) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var v = hasDay ? await _editor.GetNumberVariantsAsync() : NumberVariantsData.Empty;
            return (d, v);
        });

        _syncingDay = true;
        try
        {
            // Keep the DayOption instances while the day set is the same, so the picker's SelectedItem stays valid.
            if (DayOptions.Count != days.Count || DayOptions.Where((o, i) => o.Number != days[i].Number).Any())
            {
                DayOptions.Clear();
                foreach (var day in days)
                    DayOptions.Add(new DayOption(day, Localization));
            }
            else
                DayOption.SyncAll(DayOptions, days);
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == _session.CurrentDay?.Number) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));

        Rows.Clear();
        foreach (var row in data.Rows)
            Rows.Add(new NumberVariantRowViewModel(row, Localization));
        IsEmpty = Rows.Count == 0;
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null || _session.CurrentDay?.Number == value.Number)
            return;
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    /// <summary>
    /// Exports the table's current on-screen view (captured by the view from the SheetTable) to CSV / Excel: shows the
    /// format modal and serialises the snapshot. Null when there is nothing to export or the user cancelled; the view
    /// runs the save dialog.
    /// </summary>
    public Task<ParticipantExportResult?> ExportAsync(CsvParticipantData view) =>
        _exportFlow.RunAsync(view, day: _session.CurrentDay, nameKey: "NumberVariants.NamePart");
}

/// <summary>One table row: a number and leg with its variation.</summary>
public sealed class NumberVariantRowViewModel(NumberVariantRow row, ILocalizationService localization)
{
    public NumberVariantRow Model { get; } = row;

    public string GroupName => Model.GroupName.Length > 0 ? Model.GroupName : localization.Get("NumberVariants.NoGroup");

    public string Bib => Model.Bib;

    /// <summary>Sorts by number, then leg.</summary>
    public int BibSort => Model.Number * 100 + Math.Min(Model.Leg, 99);

    public string Course => Model.Course;

    public string CourseOrder => Model.CourseOrder;

    public string TeamName => Model.TeamName;

    public string Runner => Model.Runner;

    /// <summary>The variation cell is tinted: none for a «за номером етапу» leg, or a code the group has no variant for.</summary>
    public bool IsProblem => Model.Missing || Model.UnknownCourse;

    public string? ProblemTooltip =>
        Model.Missing ? localization.Get("NumberVariants.Missing")
        : Model.UnknownCourse ? localization.Get("NumberVariants.Unknown")
        : null;
}
