using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Spreadsheet-like control-point (КП) reference for the CURRENT competition day. Cells
/// auto-save in the background (debounced per row) — there is no Save button and no global
/// busy overlay, so editing never blocks the UI. Opened from the "Competition" top menu.
/// </summary>
public sealed partial class ControlPointsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IXmlImportFlow _importFlow;
    private readonly IBusyService _busy;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public ControlPointsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IXmlImportFlow importFlow,
        IBusyService busy,
        IDisciplineStrategyProvider strategies,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _importFlow = importFlow;
        _busy = busy;
        _strategies = strategies;
        _dialogs = dialogs;
        // Singleton VM: when the competition/day changes, drop the previous event's rows so the
        // page never shows stale data before it is next opened. The event can be raised on a pool
        // thread (session writes run inside RunAsync), so marshal LoadAsync onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.ControlPoints";
    public override string TitleKey => "Page.ControlPoints.Title";
    public override string TextKey => "Page.ControlPoints.Text";

    public ObservableCollection<ControlPointRowViewModel> Points { get; } = [];

    /// <summary>Selectable days for the top-right day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private ControlPointRowViewModel? _selectedPoint;

    /// <summary>
    /// Whether control points carry a per-point "points" value on this day: true when the day's
    /// default discipline scores points, or when at least one group on the day does. Decided entirely
    /// through the discipline strategies (no per-type conditionals here).
    /// </summary>
    [ObservableProperty]
    private bool _showPointsColumn;

    /// <summary>
    /// Coordinate display mode. True (default) shows relative "by map" coordinates — ground metres
    /// derived from the imported map position and scale; false shows the real WGS-84 latitude/longitude.
    /// Toggled by the header switch; the view rebuilds the columns when it changes.
    /// </summary>
    [ObservableProperty]
    private bool _showMapCoordinates = true;

    partial void OnShowMapCoordinatesChanged(bool value) => ColumnsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when <see cref="ShowPointsColumn"/> or the coordinate mode may have changed; the
    /// view re-applies column visibility (columns live outside the visual tree).</summary>
    public event EventHandler? ColumnsChanged;

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    /// <summary>Reloads the control points for the current day. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        // Both BD reads run off the UI thread; every collection/property write below happens
        // afterwards on the UI thread (SQLite has no real async I/O, so this can't stay inline).
        var hasDay = _session.CurrentDay is not null;
        var (days, points, groups) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var p = hasDay ? await _editor.GetControlPointsAsync() : (IReadOnlyList<ControlPoint>)[];
            var g = hasDay ? await _editor.GetGroupDayRowsAsync() : (IReadOnlyList<GroupDayRow>)[];
            return (d, p, g);
        });

        Points.Clear();
        SelectedPoint = null;

        _syncingDay = true;
        try
        {
            // Rebuild the options only when the day set actually changed; otherwise keep the
            // existing DayOption instances so the ComboBox's SelectedItem stays a valid reference
            // (a fresh list would leave the picker showing nothing after a day switch).
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

        foreach (var point in points)
            Points.Add(new ControlPointRowViewModel(point, Localization, RequestRowSave));

        UpdatePointsColumn(groups);
    }

    // Points are shown when the day's default discipline scores points, or any group on the day
    // (via its effective discipline) does — asked through the strategies, never a per-type switch.
    private void UpdatePointsColumn(IReadOnlyList<GroupDayRow> groups)
    {
        var dayDefault = _session.CurrentDay?.DefaultDiscipline;
        var byDay = dayDefault is not null && _strategies.For(dayDefault.Value).UsesControlPointPoints;
        var byGroup = groups.Any(g =>
            _strategies.For(g.DisciplineOverride ?? g.DayDefaultDiscipline).UsesControlPointPoints);

        ShowPointsColumn = byDay || byGroup;
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    // True when the current options already represent exactly these days (same count and numbers,
    // in order), so the list can be reused instead of rebuilt.
    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        if (DayOptions.Count != days.Count)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i].Number != days[i].Number)
                return false;
        return true;
    }

    // Driven by the day ComboBox. Switching the session's day re-raises SessionChanged, which
    // reloads this page; the _syncingDay guard stops LoadAsync's reassignment from re-entering.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
            return;

        _ = SwitchDayAsync(value.Day);
    }

    // Persisting the new day writes the last-session row (a synchronous SQLite write), so it goes
    // through the busy overlay/offload like every other DB access; otherwise the UI thread would
    // block on the write before SessionChanged → LoadAsync even runs.
    private async Task SwitchDayAsync(EventDay day)
    {
        await _busy.RunAsync(() => _session.SetCurrentDayAsync(day));
    }

    [RelayCommand]
    private async Task AddPointAsync()
    {
        if (_session.CurrentDay is null)
            return;

        // Persist immediately so the new row carries its real id for later debounced updates.
        var entity = await _busy.RunAsync(() => _editor.AddControlPointAsync());
        Points.Add(new ControlPointRowViewModel(entity, Localization, RequestRowSave));
    }

    /// <summary>
    /// Runs the single "Import from XML" action (chosen by the view's file picker): the shared flow
    /// parses the file, shows the two-toggle modal, and imports both control points and groups for
    /// the current day. This page then reloads its control points.
    /// </summary>
    public async Task ImportFromXmlAsync(string xml, string? fileName = null, byte[]? content = null)
    {
        if (await _importFlow.RunAsync(xml, fileName, content))
            await LoadAsync();
    }

    // The grid's delete button binds to this command. A plain click asks for confirmation first;
    // Ctrl+Click routes through DeletePointNoConfirm. The Delete key on a selected row is routed
    // through DeleteSelectedPointAsync in the view.
    [RelayCommand]
    private Task DeletePointAsync(ControlPointRowViewModel? row) => RemovePointAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeletePointNoConfirmAsync(ControlPointRowViewModel? row) => RemovePointAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected control point (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedPointAsync(bool skipConfirm) => RemovePointAsync(SelectedPoint, skipConfirm);

    private async Task RemovePointAsync(ControlPointRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "ControlPoints.Delete.ConfirmTitle",
                messageKey: "ControlPoints.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately and run the SQLite delete in the background — the user
        // never waits on the DB for a delete. If the removed row was the focused one, move the
        // selection onto its neighbour so the grid keeps a sensible focus instead of clearing it.
        if (ReferenceEquals(SelectedPoint, row))
            SelectedPoint = GridSelection.NeighbourAfterRemoval(Points, row);
        Points.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteControlPointAsync(id));

        // The confirmation modal stole keyboard focus to the overlay; pull it back to the grid
        // (now on the new selected row) so focus doesn't end up on the top menu.
        if (confirmed)
            RequestGridFocus();
    }

    // Invoked by a row on every edit (UI thread). Resets that row's debounce timer.
    private void RequestRowSave(ControlPointRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token); // fire-and-forget; the UI is never blocked
    }

    private async Task SaveRowDebouncedAsync(ControlPointRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            // ToEntity() reads VM state, so snapshot it here (UI thread) before offloading the
            // synchronous SQLite write to the pool. Autosave bypasses the busy overlay on purpose.
            var entity = row.ToEntity();
            await Task.Run(() => _editor.UpdateControlPointAsync(entity, token), token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit (or the page reloaded) — ignore.
        }
        catch
        {
            // Background save failed; never crash the UI over an autosave.
        }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}
