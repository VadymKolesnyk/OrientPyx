using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Старт → Масовий старт»: one start time per group — every runner of a checked group starts at it. A relay group
/// starts its leg 1 there and also takes a restart time per later leg (Р-29), one column per leg up to the day's
/// longest relay. Nothing is written until «Призначити старт»: then the runners' start times
/// (<c>ParticipantDay.StartTime</c>) and the relay groups' mass start + restarts are saved
/// (<see cref="ICompetitionEditorService.ApplyMassStartAsync"/>). The typed values are remembered per day
/// (<see cref="DrawSettingsKind.MassStart"/>); a group with nothing remembered starts from what the competition
/// already has (the relay group's mass start and restarts, else the runners' common start).
/// </summary>
public sealed partial class MassStartDrawViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;

    private const string DefaultStart = "11:00:00";

    // Guards SelectedDay sync during LoadAsync so the setter doesn't trigger a reload mid-load.
    private bool _syncingDay;

    // Suppresses the auto-save while the table is being (re)filled.
    private bool _loading;

    // The day the current rows belong to; what the auto-save writes to.
    private Guid? _settingsDayId;

    public MassStartDrawViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock,
        IDataChangeBus bus,
        IWorkspaceMode mode)
        : base(localization)
    {
        UseDayLock(dayLock);
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;

        // Singleton VM: reload the day list + groups on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        ObserveData(bus, session, mode,
            DataArea.Participants, DataArea.ParticipantDays, DataArea.Groups, DataArea.GroupDaySettings,
            DataArea.Days, DataArea.Relay);
    }

    /// <summary>Per-competition table-view store; persists this page's column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.MassStartDraw";
    public override string TitleKey => "Page.MassStartDraw.Title";
    public override string TextKey => "Page.MassStartDraw.Text";

    // Lucide "flag".
    public override string IconData =>
        "M4 22V4 M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z";

    // ── Day picker

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Table

    public ObservableCollection<MassStartDrawGroupRowViewModel> Groups { get; } = [];

    /// <summary>The legs that get a restart column (2…the day's longest relay); empty without a relay group. The
    /// view rebuilds its columns when this changes.</summary>
    [ObservableProperty]
    private IReadOnlyList<int> _restartLegs = [];

    /// <summary>Some group on the day is a relay (shows the «Етапів» column).</summary>
    [ObservableProperty]
    private bool _hasRelay;

    /// <summary>The start «Задати обраним» copies onto every checked group.</summary>
    [ObservableProperty]
    private string _commonStart = DefaultStart;

    /// <summary>Status line next to the action buttons.</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <inheritdoc />
    /// <remarks>Another station changed the day: re-read the groups, keeping what is typed on the page.</remarks>
    protected override Task ReloadAsync(CancellationToken cancellationToken = default)
        => ReloadGroupsAsync(preserveEdits: true);

    public async Task LoadAsync()
    {
        var days = await _busy.RunAsync(() => _editor.GetDaysAsync());

        _syncingDay = true;
        try
        {
            DayOptions.Clear();
            foreach (var day in days)
                DayOptions.Add(new DayOption(day, Localization));

            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));
        RefreshDayLock();

        StatusMessage = string.Empty;
        await ReloadGroupsAsync();
    }

    // Driven by the day ComboBox: the chosen day becomes the session's day (like the other draw pages).
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
        {
            _ = ReloadGroupsAsync();
            return;
        }
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    // Loads the selected day's groups into the table. Each row's checkbox, start and restarts come from what the
    // page remembers for the day (or, with preserveEdits, from the rows on screen), else from the competition's
    // current values, else the defaults.
    private async Task ReloadGroupsAsync(bool preserveEdits = false)
    {
        if (SelectedDay?.Day is not { } day)
        {
            ClearGroups();
            _settingsDayId = null;
            return;
        }

        var onScreen = preserveEdits && _settingsDayId == day.Id && Groups.Count > 0 ? RowSettings() : null;
        var data = await _busy.RunAsync(() => _editor.GetMassStartDataAsync(day.Id));
        DrawSettings? saved = null;
        if (onScreen is null)
        {
            saved = await _busy.RunAsync(() => _editor.GetDrawSettingsAsync(day.Id, DrawSettingsKind.MassStart));
            onScreen = saved?.Rows;
        }
        var stored = new Dictionary<Guid, DrawGroupRowSettings>();
        foreach (var row in onScreen ?? [])
            stored.TryAdd(row.GroupId, row);

        _loading = true;
        try
        {
            ClearGroups();
            _settingsDayId = day.Id;
            if (saved is not null)
                CommonStart = saved.GlobalStart;

            var maxLegs = data.Groups.Where(g => g.IsRelay).Select(g => g.LegCount).DefaultIfEmpty(1).Max();
            var legs = Enumerable.Range(2, Math.Max(0, maxLegs - 1)).ToList();

            foreach (var group in data.Groups)
            {
                stored.TryGetValue(group.GroupId, out var row);
                var start = row?.Start
                            ?? NonBlank(StartTimeFormat.Format(group.IsRelay ? group.MassStartTime ?? group.CurrentStart : group.CurrentStart))
                            ?? DefaultStart;
                var cells = legs.Select(leg => new MassStartRestartCellViewModel(
                        leg,
                        group.IsRelay && leg <= group.LegCount,
                        row is not null && row.Restarts.TryGetValue(leg, out var typed)
                            ? typed
                            : StartTimeFormat.Format(group.RestartTimes.TryGetValue(leg, out var rt) ? rt : null)))
                    .ToList();

                var vm = new MassStartDrawGroupRowViewModel(group, start, CurrentStartLabel(group), cells)
                {
                    Selected = row?.Selected ?? group.MemberCount > 0,
                };
                vm.SettingsChanged += SaveSettings;
                Groups.Add(vm);
            }

            HasRelay = data.Groups.Any(g => g.IsRelay);
            if (!RestartLegs.SequenceEqual(legs))
                RestartLegs = legs;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string? NonBlank(string text) => text.Length > 0 ? text : null;

    // «10:00:00», «різні» or blank; a relay group adds its current restarts.
    private string CurrentStartLabel(MassStartGroup group)
    {
        var label = group.MixedStarts
            ? Localization.Get("MassStartDraw.Current.Mixed")
            : StartTimeFormat.Format(group.CurrentStart);
        if (!group.IsRelay || group.RestartTimes.Count == 0)
            return label;
        var restarts = string.Join(", ", group.RestartTimes
            .Where(kv => kv.Key <= group.LegCount)
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key} — {StartTimeFormat.Format(kv.Value)}"));
        if (restarts.Length == 0)
            return label;
        var text = string.Format(Localization.Get("MassStartDraw.Current.Restarts"), restarts);
        return label.Length > 0 ? $"{label}; {text}" : text;
    }

    // The rows as the page remembers them.
    private List<DrawGroupRowSettings> RowSettings() => Groups
        .Select(g => new DrawGroupRowSettings
        {
            GroupId = g.GroupId,
            Selected = g.Selected,
            Start = g.Start,
            Restarts = g.Restarts.Where(c => c.IsEnabled).ToDictionary(c => c.Leg, c => c.Text),
        })
        .ToList();

    private void SaveSettings()
    {
        if (_loading || _settingsDayId is not { } dayId)
            return;
        var settings = new DrawSettings { GlobalStart = CommonStart, Rows = RowSettings() };
        // Fire-and-forget: the remembered values are a convenience, a failed write must not block the page.
        _ = _editor.SaveDrawSettingsAsync(dayId, DrawSettingsKind.MassStart, settings);
    }

    partial void OnCommonStartChanged(string value) => SaveSettings();

    // ── Commands

    [RelayCommand]
    private void SelectAll() => SetAll(row => row.Selected = true);

    [RelayCommand]
    private void SelectNone() => SetAll(row => row.Selected = false);

    /// <summary>Copies <see cref="CommonStart"/> onto every checked group.</summary>
    [RelayCommand]
    private void ApplyCommonStart()
    {
        StatusMessage = string.Empty;
        if (!StartTimeFormat.TryParse(CommonStart, out var parsed) || parsed is not { } start)
        {
            StatusMessage = Localization.Get("Draw.Error.Start");
            return;
        }
        var text = StartTimeFormat.Format(start);
        SetAll(row =>
        {
            if (row.Selected)
                row.Start = text;
        });
    }

    // Changes every row at once, persisting the table only once at the end.
    private void SetAll(Action<MassStartDrawGroupRowViewModel> change)
    {
        _loading = true;
        try
        {
            foreach (var row in Groups)
                change(row);
        }
        finally
        {
            _loading = false;
        }
        SaveSettings();
    }

    /// <summary>
    /// Validates the checked groups' start (and a relay group's restarts — each later than the start and than the
    /// previous leg's restart), asks to confirm, then writes the mass start onto the day.
    /// </summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        StatusMessage = string.Empty;
        if (SelectedDay?.Day is not { } day || !await EnsureDayEditableAsync())
            return;

        var selected = Groups.Where(g => g.Selected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = Localization.Get("MassStartDraw.Error.NoGroups");
            return;
        }

        var assignments = new List<MassStartAssignment>(selected.Count);
        foreach (var row in selected)
        {
            if (!StartTimeFormat.TryParse(row.Start, out var parsed) || parsed is not { } start)
            {
                StatusMessage = string.Format(Localization.Get("ClassicDraw.Error.Start"), row.Name);
                return;
            }

            Dictionary<int, TimeSpan>? restarts = null;
            if (row.IsRelay)
            {
                restarts = [];
                var previous = start;
                foreach (var cell in row.Restarts.Where(c => c.IsEnabled).OrderBy(c => c.Leg))
                {
                    if (string.IsNullOrWhiteSpace(cell.Text))
                        continue;
                    if (!StartTimeFormat.TryParse(cell.Text, out var r) || r is not { } restart)
                    {
                        StatusMessage = string.Format(Localization.Get("MassStartDraw.Error.Restart"), row.Name, cell.Leg);
                        return;
                    }
                    if (restart <= previous)
                    {
                        StatusMessage = string.Format(Localization.Get("MassStartDraw.Error.RestartOrder"), row.Name, cell.Leg);
                        return;
                    }
                    restarts[cell.Leg] = restart;
                    previous = restart;
                }
            }
            assignments.Add(new MassStartAssignment(row.GroupId, start, restarts));
        }

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "MassStartDraw.Run.ConfirmTitle",
            messageKey: selected.Any(r => r.IsRelay)
                ? "MassStartDraw.Run.ConfirmMessageRelay"
                : "MassStartDraw.Run.ConfirmMessage",
            confirmKey: "MassStartDraw.Run.Confirm",
            cancelKey: "Common.Cancel"));
        if (!confirmed)
            return;

        var result = await _busy.RunAsync(() => _editor.ApplyMassStartAsync(day.Id, assignments));
        await ReloadGroupsAsync(preserveEdits: true);
        StatusMessage = result.RelayGroups > 0
            ? string.Format(Localization.Get("MassStartDraw.SavedRelay"), result.Starts, result.RelayGroups)
            : string.Format(Localization.Get("MassStartDraw.Saved"), result.Starts);
    }

    private void ClearGroups()
    {
        foreach (var row in Groups)
            row.SettingsChanged = null;
        Groups.Clear();
    }
}
