using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
    private readonly IIofXmlParser _xmlParser;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public ControlPointsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IIofXmlParser xmlParser,
        IDialogService dialogs,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _xmlParser = xmlParser;
        _dialogs = dialogs;
        _busy = busy;
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
        var (days, points) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var p = hasDay ? await _editor.GetControlPointsAsync() : (IReadOnlyList<ControlPoint>)[];
            return (d, p);
        });

        Points.Clear();

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
        if (_syncingDay || value is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
            return;

        _ = _session.SetCurrentDayAsync(value.Day);
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

    /// <summary>Stable key for the single toggle on the control-points import modal.</summary>
    private const string ReplaceAllOptionKey = "replaceAll";

    /// <summary>
    /// Imports control points from IOF XML text (chosen by the view's file picker). Shows the
    /// shared import-options modal first: its one toggle, "replace all" (default on), decides
    /// whether the day's points are fully replaced or only new codes are appended. The actual
    /// parse + write run under the global busy overlay. Returns silently if no day is selected.
    /// </summary>
    public async Task ImportFromXmlAsync(string xml)
    {
        if (_session.CurrentDay is null || string.IsNullOrWhiteSpace(xml))
            return;

        // Parse up front so a malformed file is reported before the user sees the options modal.
        // Parsing is CPU-bound and synchronous (XDocument + coordinate projection), so it runs
        // off the UI thread to avoid the freeze on large files.
        var outcome = await _busy.RunAsync(() => Task.FromResult(ParseXml(xml)));

        if (!outcome.Success)
        {
            await _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
                Localization,
                titleKey: "ControlPoints.Import.Title",
                messageKey: "ControlPoints.Import.Error",
                options: []));
            return;
        }

        var dialog = new ImportOptionsViewModel(
            Localization,
            titleKey: "ControlPoints.Import.Title",
            messageKey: "ControlPoints.Import.Message",
            options: [new ImportOption(ReplaceAllOptionKey, "ControlPoints.Import.ReplaceAll", isChecked: true)]);

        var result = await _dialogs.ShowImportOptionsAsync(dialog);
        if (result is null)
            return; // cancelled

        var replaceAll = result.Get(ReplaceAllOptionKey, fallback: true);
        await _busy.RunAsync(() => _editor.ImportControlPointsAsync(outcome.Data!, replaceAll));
        // LoadAsync wraps its own BD reads in RunAsync and writes the collections on the UI thread.
        await LoadAsync();
    }

    // Runs the synchronous parser and wraps success/failure so it can be marshalled off the UI thread.
    private IofCourseDataParseOutcome ParseXml(string xml)
    {
        try
        {
            return IofCourseDataParseOutcome.Ok(_xmlParser.Parse(xml));
        }
        catch (IofXmlFormatException ex)
        {
            return IofCourseDataParseOutcome.Failed(ex.Message);
        }
    }

    // Small local outcome wrapper so a parse failure can be surfaced through the same modal path.
    private readonly struct IofCourseDataParseOutcome
    {
        private IofCourseDataParseOutcome(bool success, BusinessLogic.Models.IofCourseData? data, string? error)
        {
            Success = success;
            Data = data;
            Error = error;
        }

        public bool Success { get; }
        public BusinessLogic.Models.IofCourseData? Data { get; }
        public string? Error { get; }

        public static IofCourseDataParseOutcome Ok(BusinessLogic.Models.IofCourseData data) => new(true, data, null);
        public static IofCourseDataParseOutcome Failed(string error) => new(false, null, error);
    }

    [RelayCommand]
    private async Task DeletePointAsync(ControlPointRowViewModel? row)
    {
        if (row is null)
            return;

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        await _busy.RunAsync(() => _editor.DeleteControlPointAsync(row.Id));
        Points.Remove(row);
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
