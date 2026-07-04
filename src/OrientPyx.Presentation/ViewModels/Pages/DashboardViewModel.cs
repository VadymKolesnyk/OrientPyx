using System.Collections.ObjectModel;
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
/// A dashboard quick action — a shortcut to one of the pages most used while running a competition.
/// The dashboard raises <see cref="DashboardViewModel.QuickActionRequested"/> with one of these; the
/// host (MainWindowViewModel) owns the page-open commands and routes it, since the dashboard sits
/// under the shell and has no reference to them.
/// </summary>
public enum DashboardQuickAction
{
    Participants,
    FinishRead,
    Draw,
    StartProtocol,
    Protocols,
    Splits,
    Groups,
    Chips,
    ParticipantsWithoutChip,
    ParticipantsWithoutGroup,
    ParticipantsOnCourse,
}

/// <summary>
/// The «Панель» dashboard: an overview of the selected competition and the current day with live
/// counts, plus quick-action buttons into the pages used most during a competition.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;

    [ObservableProperty]
    private DashboardInfo _info = new() { HasSelection = false };

    /// <summary>Selectable competition days for the day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    // While the dashboard is the visible page, its live counts (finished / on course / read-outs)
    // must keep up with read-outs streaming in at the finish. LoadAsync alone only runs on open /
    // day-switch, so a user sitting on the page would see frozen tiles. This timer re-loads on an
    // interval; the host (MainWindowViewModel) starts it when the dashboard is shown and stops it
    // when another page takes over, so it never polls the DB in the background.
    private readonly DispatcherTimer _refreshTimer;

    public DashboardViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;

        // The active competition / day drives every figure here; reload whenever it changes (a day
        // switch, a new selection, or returning to the picker). Hop to the UI thread — SessionChanged
        // may fire off a pool thread.
        _session.SessionChanged += OnSessionChanged;
        // The discipline name + "День N" label are localized; re-raise them on a language switch.
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisciplineName));
            OnPropertyChanged(nameof(DayLabel));
        };

        // Ticks only while the dashboard is the shown page (see Start/StopAutoRefresh). A short
        // interval keeps the "фінішувало / на дистанції" tiles honest during finish read-out without
        // hammering the event DB — one lightweight recompute a few seconds apart.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => _ = LoadAsync();

        _ = LoadAsync();
    }

    /// <summary>
    /// Begin auto-refreshing the live counts (called by the host when the dashboard becomes the
    /// visible page). Reloads once immediately so the tiles are current the moment it is shown, then
    /// on the timer interval.
    /// </summary>
    public void StartAutoRefresh()
    {
        _ = LoadAsync();
        _refreshTimer.Start();
    }

    /// <summary>Stop auto-refreshing (called by the host when another page takes over).</summary>
    public void StopAutoRefresh() => _refreshTimer.Stop();

    public override string NavKey => "Nav.Dashboard";
    public override string TitleKey => "Page.Dashboard.Title";
    public override string TextKey => "Page.Dashboard.Text";

    // Dashboard tiles.
    public override string IconData =>
        "M4,4 h7 v7 h-7 z M13,4 h7 v4 h-7 z M13,10 h7 v10 h-7 z M4,13 h7 v7 h-7 z";

    /// <summary>
    /// Raised when a quick-action button is pressed. The host opens the matching page (the dashboard
    /// cannot navigate on its own — see <see cref="DashboardQuickAction"/>).
    /// </summary>
    public event EventHandler<DashboardQuickAction>? QuickActionRequested;

    /// <summary>Localized name of the current day's discipline (вид змагань).</summary>
    public string DisciplineName => Localization.Get("Discipline.Type." + Info.CurrentDayDiscipline);

    /// <summary>"День {N}" header for the current-day card.</summary>
    public string DayLabel => string.Format(Localization.Get("Dashboard.Today.Day"), Info.CurrentDayNumber);

    /// <summary>Earliest start time "hh:mm:ss", or a dash when nothing is drawn yet.</summary>
    public string FirstStartText => FormatStart(Info.FirstStart);

    /// <summary>Latest start time "hh:mm:ss", or a dash when nothing is drawn yet.</summary>
    public string LastStartText => FormatStart(Info.LastStart);

    private static string FormatStart(TimeSpan? value)
    {
        var text = StartTimeFormat.Format(value);
        return text.Length == 0 ? "—" : text;
    }

    public async Task LoadAsync()
    {
        Info = await _editor.GetDashboardAsync();

        // Sync the day picker to the session (guarded so the SelectedDay setter doesn't switch the day).
        var days = _session.CurrentEvent is null
            ? (IReadOnlyList<EventDay>)[]
            : await _editor.GetDaysAsync();

        _syncingDay = true;
        try
        {
            if (!SameDays(days))
            {
                DayOptions.Clear();
                foreach (var day in days)
                    DayOptions.Add(new DayOption(day, Localization));
            }

            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));
    }

    // True when the current options already represent exactly these days (same count and numbers).
    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        if (DayOptions.Count != days.Count)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i].Number != days[i].Number)
                return false;
        return true;
    }

    // Driven by the day ComboBox; switches the session's day (the guard stops LoadAsync's sync re-entering).
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
            return;

        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    [RelayCommand]
    private void QuickAction(DashboardQuickAction action)
        => QuickActionRequested?.Invoke(this, action);

    partial void OnInfoChanged(DashboardInfo value)
    {
        OnPropertyChanged(nameof(DisciplineName));
        OnPropertyChanged(nameof(DayLabel));
        OnPropertyChanged(nameof(FirstStartText));
        OnPropertyChanged(nameof(LastStartText));
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionChanged(sender, e));
            return;
        }

        _ = LoadAsync();
    }
}
