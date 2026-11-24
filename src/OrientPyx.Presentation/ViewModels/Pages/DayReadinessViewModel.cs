using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Старт → Перевірка готовності дня»: every problem of the session day's data in one table (errors first) —
/// <see cref="ICompetitionEditorService.GetDayReadinessAsync"/>. Each row's «Перейти» (and F8) opens the page the
/// problem is fixed on and selects its row there; the root coordinator routes it (<see cref="GoToRequested"/>).
/// Read-only: nothing here writes, so there is no day lock and no undo. Live: another station's writes reload it, and
/// it reloads each time it is opened (this machine's own writes on other pages are not on the bus for it).
/// </summary>
public sealed partial class DayReadinessViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private bool _syncingDay;
    private int _dayNumber;

    public DayReadinessViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        ITableLayoutStore layoutStore,
        IDataChangeBus bus,
        IWorkspaceMode mode,
        IPluginCatalog plugins)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        LayoutStore = layoutStore;
        GoToCommand = new RelayCommand<ReadinessRowViewModel?>(GoTo);

        // Another competition or day: reload while shown (the open command loads it otherwise).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (IsActive)
                _ = LoadAsync();
        });
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Days, DataArea.ControlPoints, DataArea.Groups, DataArea.GroupDaySettings, DataArea.CourseVariants,
            DataArea.Participants, DataArea.ParticipantDays, DataArea.CustomFields, DataArea.Relay);
        // A plugin switched on or off changes which disciplines are known.
        plugins.EnabledChanged += (_, _) => RequestReload();
        localization.PropertyChanged += (_, _) => RefreshTexts();
    }

    public override string NavKey => "Nav.DayReadiness";
    public override string TitleKey => "Page.DayReadiness.Title";
    public override string TextKey => "Page.DayReadiness.Text";

    // Lucide "clipboard-check".
    public override string IconData =>
        "M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2 M9 2h6a1 1 0 0 1 1 1v2a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z M9 14l2 2 4-4";

    /// <summary>Persists the table's column layout. Bound by the view.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    /// <summary>The day's problems, errors first.</summary>
    public ObservableCollection<ReadinessRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private ReadinessRowViewModel? _selectedRow;

    /// <summary>«Помилок: X · Попереджень: Y», or «Проблем не знайдено»; empty with no day.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Some problem is an error (the summary reads red).</summary>
    [ObservableProperty]
    private bool _hasErrors;

    /// <summary>Only warnings (the summary reads amber).</summary>
    [ObservableProperty]
    private bool _hasOnlyWarnings;

    /// <summary>The day has no problem at all (the summary reads green, the table is hidden).</summary>
    [ObservableProperty]
    private bool _isClean;

    /// <summary>Raised by «Перейти»: open the page the problem is fixed on and show its row (the root coordinator).</summary>
    public event EventHandler<ReadinessIssue>? GoToRequested;

    /// <summary>A row's «Перейти» button (parameter: the row).</summary>
    public IRelayCommand<ReadinessRowViewModel?> GoToCommand { get; }

    /// <summary>The table's F8 / context-menu action: «Перейти» for a single selected row.</summary>
    public string? GoToActionLabel(IReadOnlyList<object> rows) =>
        rows.Count == 1 && rows[0] is ReadinessRowViewModel ? Localization.Get("DayReadiness.GoTo.Action") : null;

    public void GoTo(IReadOnlyList<object> rows)
    {
        if (rows.Count == 1 && rows[0] is ReadinessRowViewModel row)
            GoTo(row);
    }

    private void GoTo(ReadinessRowViewModel? row)
    {
        if (row is not null)
            GoToRequested?.Invoke(this, row.Issue);
    }

    /// <inheritdoc />
    protected override Task ReloadAsync(CancellationToken cancellationToken = default) => LoadAsync();

    public async Task LoadAsync()
    {
        var day = _session.CurrentDay;
        var (days, readiness) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var r = day is null ? DayReadiness.Empty : await _editor.GetDayReadinessAsync(day.Id);
            return (d, r);
        });

        SyncDays(days);
        _dayNumber = readiness.DayNumber;
        var issues = readiness.Issues.DistinctBy(i => i.Key).ToList();
        RowSync.Sync(Rows, issues,
            itemKey: i => i.Key,
            rowKey: r => r.Key,
            create: i => new ReadinessRowViewModel(i, Localization, _dayNumber),
            update: (row, i) => row.Apply(i, Localization, _dayNumber));
        if (SelectedRow is { } selected && !Rows.Contains(selected))
            SelectedRow = null;
        RefreshSummary(day is not null, readiness.Errors, readiness.Warnings);
    }

    private void SyncDays(IReadOnlyList<EventDay> days)
    {
        _syncingDay = true;
        try
        {
            // Keep the DayOption instances while the day set is the same, so the picker's SelectedItem stays valid.
            if (DayOptions.Count != days.Count || DayOptions.Where((o, i) => o.Number != days[i].Number).Any())
            {
                DayOptions.Clear();
                foreach (var d in days)
                    DayOptions.Add(new DayOption(d, Localization));
            }
            else
            {
                DayOption.SyncAll(DayOptions, days);
            }
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == _session.CurrentDay?.Number) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));
    }

    // The picker switches the session's day, as on every per-day page; SessionChanged reloads the list.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null || _session.CurrentDay?.Number == value.Number)
            return;
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    private (bool HasDay, int Errors, int Warnings) _counts;

    private void RefreshSummary(bool hasDay, int errors, int warnings)
    {
        _counts = (hasDay, errors, warnings);
        HasErrors = errors > 0;
        HasOnlyWarnings = errors == 0 && warnings > 0;
        IsClean = hasDay && errors == 0 && warnings == 0;
        Summary = !hasDay ? string.Empty
            : IsClean ? Localization.Get("DayReadiness.Empty")
            : string.Format(CultureInfo.CurrentCulture, Localization.Get("DayReadiness.Summary"), errors, warnings);
    }

    // A language switch: the rows' texts and the summary (the column headers are rebuilt by the view).
    private void RefreshTexts()
    {
        foreach (var row in Rows)
            row.Refresh(Localization, _dayNumber);
        RefreshSummary(_counts.HasDay, _counts.Errors, _counts.Warnings);
    }
}
