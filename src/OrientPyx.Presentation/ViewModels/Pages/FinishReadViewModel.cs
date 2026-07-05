using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Finish read-out log for the CURRENT competition day. Watches a readout CSV (default
/// <c>day{N}/event.csv</c>) every N seconds as a background process — like the chip rental auto-read —
/// and appends each newly-read chip to a non-editable per-day log. Each row resolves to the participant
/// who holds that chip on the day (number / ПІБ / group), or a "невідомий" marker when unrecognised.
/// Re-running the read never doubles rows: a record already logged (by content) is skipped.
/// </summary>
public sealed partial class FinishReadViewModel : PageViewModelBase
{
    private const string DefaultReadoutFileName = "event.csv";

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IReadoutParserSelector _readoutParsers;
    private readonly IFileReadoutPoller _poller;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IBackgroundActivityService _activities;
    private readonly IAppSettingsService _appSettings;
    private readonly ISplitPrintService _printer;
    private readonly IActivityLog _log;

    /// <summary>Top-bar activity handle while auto-read runs; null when off.</summary>
    private FinishReadActivity? _activity;

    /// <summary>The app-level readout format, refreshed on load; drives which "no file" hint is shown.</summary>
    private ReadoutType _readoutType = ReadoutType.SportIdent;

    public FinishReadViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IReadoutParserSelector readoutParsers,
        IFileReadoutPoller poller,
        IBusyService busy,
        IDialogService dialogs,
        IBackgroundActivityService activities,
        IAppSettingsService appSettings,
        ISplitPrintService printer,
        IActivityLog log,
        IUiPreferencesService preferences,
        ITableLayoutStore layoutStore)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _readoutParsers = readoutParsers;
        _poller = poller;
        _busy = busy;
        _dialogs = dialogs;
        _activities = activities;
        _appSettings = appSettings;
        _printer = printer;
        _log = log;

        // Singleton VM: on a competition/day change, stop the watch (the file belongs to the old day)
        // and reload. SessionChanged may be raised on a pool thread, so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StopAutoRead();
            _ = LoadAsync();
        });

        Splits = new FinishSplitsViewModel(localization, preferences);
    }

    public override string NavKey => "Nav.FinishRead";
    public override string TitleKey => "Page.FinishRead.Title";
    public override string TextKey => "Page.FinishRead.Text";

    /// <summary>Raised when the user clicks "go to settings" on the top-bar activity — asks the shell to show this page.</summary>
    public event EventHandler? NavigateToSelfRequested;

    /// <summary>The per-competition table-view store, bound by the log table so its column
    /// order/width/visibility persist to <c>events/&lt;id&gt;/views.json</c>.</summary>
    public ITableLayoutStore LayoutStore { get; }

    /// <summary>The day's read log, newest at the bottom (ordered by sequence). Read-only rows.</summary>
    public ObservableCollection<FinishReadRowViewModel> Readouts { get; } = [];

    /// <summary>The passage/splits panel under the table, filled for the selected row (see <see cref="SelectedReadout"/>).</summary>
    public FinishSplitsViewModel Splits { get; }

    /// <summary>The currently selected log row; selecting one loads its splits, deselecting clears them.</summary>
    [ObservableProperty]
    private FinishReadRowViewModel? _selectedReadout;

    /// <summary>The competition's rental-chip numbers, so the chip column bold-reds a non-rental chip
    /// the same way the participants table does.</summary>
    public RentalChipRegistry RentalChips { get; } = new();

    /// <summary>Selectable days for the top-right day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    /// <summary>
    /// True when the current day scores points (rogaine, or any group overriding to a scored format) —
    /// drives the table's optional «Бали» column. Raised so the View rebuilds its bands when it flips.
    /// </summary>
    [ObservableProperty]
    private bool _showScoreColumn;

    // --- Auto-read panel (in-memory only; never persisted, per the session rule) -----------------

    [ObservableProperty]
    private bool _isAutoReadExpanded = true;

    [ObservableProperty]
    private string _autoReadFilePath = string.Empty;

    [ObservableProperty]
    private int _autoReadIntervalSeconds = 5;

    [ObservableProperty]
    private bool _autoReadEnabled;

    [ObservableProperty]
    private bool _isAutoReadPaused;

    /// <summary>
    /// When on, each newly-read row is printed to the configured printer as it arrives (the same split
    /// printout the «друк» action makes). In-memory only, like the rest of the auto-read panel. Silent:
    /// it never opens the settings modal — a tick with no printer configured just skips printing.
    /// </summary>
    [ObservableProperty]
    private bool _autoPrintEnabled;

    /// <summary>
    /// A localized problem shown as a banner on the auto-read panel, set when the watched file is
    /// missing or is not a Config+ card-readout export; empty when there's nothing to report.
    /// </summary>
    [ObservableProperty]
    private string _autoReadError = string.Empty;

    /// <summary>True when <see cref="AutoReadError"/> has text — drives the banner's visibility.</summary>
    public bool HasAutoReadError => !string.IsNullOrEmpty(AutoReadError);

    partial void OnAutoReadErrorChanged(string value) => OnPropertyChanged(nameof(HasAutoReadError));

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    [RelayCommand]
    private void ToggleAutoRead() => IsAutoReadExpanded = !IsAutoReadExpanded;

    // Wipes the current day's finish-read log after a confirmation, then reloads (clearing the splits
    // panel along with the empty selection). A no-op when no day is selected or the log is already empty.
    [RelayCommand]
    private async Task ClearReadoutsAsync()
    {
        if (_session.CurrentDay is null || Readouts.Count == 0)
            return;

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "FinishRead.Clear.ConfirmTitle",
            messageKey: "FinishRead.Clear.ConfirmMessage"));
        if (!confirmed)
            return;

        await _busy.RunAsync(() => _editor.ClearFinishReadoutsAsync());
        await LoadAsync();
    }

    /// <summary>Reloads the day picker and the current day's log. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        var hasDay = _session.CurrentDay is not null;
        var (days, rows, chips, usesScore, readoutType) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var r = hasDay ? await _editor.GetFinishReadoutRowsAsync() : (IReadOnlyList<FinishReadoutRow>)[];
            var c = await _editor.GetRentalChipsAsync();
            var s = hasDay && await _editor.CurrentDayUsesScoreAsync();
            var rt = await _appSettings.GetReadoutTypeAsync();
            return (d, r, c, s, rt);
        });
        RentalChips.Reset(chips.Select(c => c.Number));
        ShowScoreColumn = usesScore;
        _readoutType = readoutType;

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

        // Default the watched file to the current day's folder (day{N}/event.csv) unless the user
        // already typed a path. Done after the day is resolved so it follows the selected day.
        SyncDefaultFilePath();

        // Preserve the selection across a reload (auto-read reloads on every new read) so the splits
        // panel doesn't flicker shut; re-select the row with the same id when it's still present.
        var selectedId = SelectedReadout?.Id;
        Readouts.Clear();
        foreach (var row in rows)
            Readouts.Add(new FinishReadRowViewModel(row, Localization));
        SelectedReadout = selectedId is { } id
            ? Readouts.FirstOrDefault(r => r.Id == id)
            : null;
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

    // Sets the default per-day readout path (day{N}/event.csv) when the field is blank or still points
    // at another day's default — so switching the day repoints the watch, but a custom path is kept.
    private void SyncDefaultFilePath()
    {
        var folder = _session.CurrentEvent?.FolderPath;
        var day = _session.CurrentDay;
        if (string.IsNullOrEmpty(folder) || day is null)
            return;

        var def = Path.Combine(DayFolders.PathFor(folder, day.Number), DefaultReadoutFileName);
        if (string.IsNullOrWhiteSpace(AutoReadFilePath) || IsADayDefaultPath(folder))
            AutoReadFilePath = def;
    }

    // True when the current path is some day's default event.csv (so it may be repointed on day switch).
    private bool IsADayDefaultPath(string folder)
    {
        var name = Path.GetFileName(AutoReadFilePath);
        var parent = Path.GetFileName(Path.GetDirectoryName(AutoReadFilePath) ?? string.Empty);
        return string.Equals(name, DefaultReadoutFileName, StringComparison.OrdinalIgnoreCase)
               && parent.StartsWith("day", StringComparison.OrdinalIgnoreCase);
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

    // Selecting a log row loads its discipline-specific splits into the bottom panel; no selection
    // clears it. An unrecognised chip still shows its raw passage (every punch in chip order) — just
    // without a prescribed course to judge it against. The build is SQLite work, so it runs off the UI thread.
    partial void OnSelectedReadoutChanged(FinishReadRowViewModel? value)
    {
        if (value is null)
        {
            Splits.Clear();
            return;
        }

        var id = value.Id;

        // Header line: «#bib  ПІБ (group) · chip XXXXX» — bib and chip prefix the name/group so the
        // panel identifies the runner at a glance (parts are dropped when blank).
        var nameAndGroup = string.IsNullOrWhiteSpace(value.GroupName)
            ? value.FullName
            : $"{value.FullName} ({value.GroupName})";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(value.ParticipantNumber))
            parts.Add($"#{value.ParticipantNumber}");
        if (!string.IsNullOrWhiteSpace(nameAndGroup))
            parts.Add(nameAndGroup);
        if (!string.IsNullOrWhiteSpace(value.ChipNumber))
            parts.Add(string.Format(Localization.Get("FinishRead.Splits.ChipLabel"), value.ChipNumber));

        var heading = string.Join("  ·  ", parts);

        _ = LoadSplitsAsync(id, heading);
    }

    private async Task LoadSplitsAsync(Guid readoutId, string heading)
    {
        var view = await _busy.RunAsync(() => _editor.GetFinishSplitsAsync(readoutId));

        // The selection may have changed while we awaited; only apply if still the chosen row.
        if (SelectedReadout?.Id != readoutId)
            return;

        if (view is null)
            Splits.Clear();
        else
            Splits.Show(view, heading);
    }

    // --- Split printout ----------------------------------------------------------------------------

    // Prints the split printout for a log row (the print icon in the «Дії» column). Prints straight to the
    // configured printer; when none is set yet (or the saved one is gone), opens the print-settings modal
    // first and prints with the freshly chosen target. A no-op on a non-Windows build (the modal/print
    // surface a localized "Windows only" message).
    [RelayCommand]
    private async Task PrintRowAsync(FinishReadRowViewModel? row)
    {
        if (row is null)
            return;

        if (!_printer.IsSupported)
        {
            await ShowInfoAsync("Print.Unsupported");
            return;
        }

        var settings = await _appSettings.GetPrintSettingsAsync();
        var installed = _printer.GetInstalledPrinters();
        // Force the settings modal when nothing is chosen or the saved printer is no longer installed.
        if (!settings.HasPrinter || !installed.Contains(settings.PrinterName))
        {
            var saved = await _dialogs.ShowPrintSettingsAsync(
                new PrintSettingsViewModel(Localization, _appSettings, _printer, settings));
            if (!saved)
                return;
            settings = await _appSettings.GetPrintSettingsAsync();
            if (!settings.HasPrinter)
                return;
        }

        try
        {
            if (!await PrintReadoutAsync(row.Id, settings))
                return;
        }
        catch (PrintNotSupportedException)
        {
            await ShowInfoAsync("Print.Unsupported");
        }
    }

    // Builds and prints the split printout for one readout id to the given printer, logging on success.
    // Returns false when there was nothing to print (the row resolved to no document). Shared by the
    // manual «друк» action and the auto-print path; the caller owns any user-facing error handling.
    private async Task<bool> PrintReadoutAsync(Guid readoutId, PrintSettings settings)
    {
        var doc = await _busy.RunAsync(() => _editor.GetSplitPrintDocumentAsync(readoutId));
        if (doc is null)
            return false;

        await _printer.PrintAsync(doc, BuildPrintLabels(), settings);
        _log.Action(string.Format(Localization.Get("FinishRead.Print.Log"), doc.ChipNumber, settings.PrinterName));
        return true;
    }

    // Opens the edit modal for a log row (the pencil in the «Дії» column): reassign the chip to another
    // participant, edit the start/finish/punch times and codes, and set a manual status. On save it
    // persists the edit (and any reassignment) and reloads so the log + splits panel reflect the change.
    [RelayCommand]
    private async Task EditRowAsync(FinishReadRowViewModel? row)
    {
        if (row is null)
            return;

        var data = await _busy.RunAsync(() => _editor.GetFinishReadoutEditAsync(row.Id));
        if (data is null)
            return;

        var edit = await _dialogs.ShowFinishReadoutEditAsync(new FinishReadoutEditViewModel(Localization, data));
        if (edit is null)
            return;

        await _busy.RunAsync(() => _editor.UpdateFinishReadoutAsync(edit));
        _log.Action(string.Format(Localization.Get("FinishRead.Edit.Log"), edit.ChipNumber));
        await LoadAsync();
    }

    // Opens the «проблемні КП» modal: tick the day's broken controls. On save it persists the disabled set
    // and reloads so the log statuses and splits recompute (a disabled control is no longer required, so a
    // runner who missed it is not penalised). A no-op when no day is selected.
    [RelayCommand]
    private async Task OpenProblematicControlsAsync()
    {
        if (_session.CurrentDay is null)
            return;

        var controls = await _busy.RunAsync(() => _editor.GetControlPointsAsync());
        var items = controls
            .Select(cp => new ProblematicControlItem(cp.Id, ControlLabel(cp), cp.IsDisabled))
            .ToList();

        var disabledIds = await _dialogs.ShowProblematicControlsAsync(
            new ProblematicControlsViewModel(Localization, items));
        if (disabledIds is null)
            return;

        await _busy.RunAsync(() => _editor.SetProblematicControlsAsync(disabledIds));
        _log.Action(string.Format(Localization.Get("FinishRead.Problematic.Log"), disabledIds.Count));
        await LoadAsync();
    }

    // The label shown for a control point in the «проблемні КП» modal: its code, plus a parenthetical type
    // hint for the start/finish markers (so they're recognisable — they don't normally count as КП).
    private string ControlLabel(BusinessLogic.Entities.ControlPoint cp)
    {
        var code = string.IsNullOrWhiteSpace(cp.Code) ? "—" : cp.Code.Trim();
        return cp.Type switch
        {
            BusinessLogic.Enums.ControlPointType.Start => $"{code} ({Localization.Get("ControlPoints.Type.Start")})",
            BusinessLogic.Enums.ControlPointType.Finish => $"{code} ({Localization.Get("ControlPoints.Type.Finish")})",
            _ => code
        };
    }

    // Opens the print-settings modal from the page header (printer + roll width), independent of a print.
    [RelayCommand]
    private async Task OpenPrintSettingsAsync()
    {
        var settings = await _appSettings.GetPrintSettingsAsync();
        await _dialogs.ShowPrintSettingsAsync(
            new PrintSettingsViewModel(Localization, _appSettings, _printer, settings));
    }

    // Localized captions the printer draws around the values (the document itself is values-only).
    private SplitPrintLabels BuildPrintLabels() => new(
        ChipLabel: Localization.Get("FinishRead.Print.Chip"),
        RentalChipLabel: Localization.Get("FinishRead.Print.RentalChip"),
        StartLabel: Localization.Get("FinishRead.Print.Start"),
        FinishLabel: Localization.Get("FinishRead.Print.Finish"),
        ResultLabel: Localization.Get("FinishRead.Print.Result"),
        DistanceLabel: Localization.Get("FinishRead.Print.Distance"),
        AvgPaceLabel: Localization.Get("FinishRead.Print.AvgPace"),
        StatusLabel: Localization.Get("FinishRead.Col.Status"),
        MpDetailLabel: Localization.Get("FinishRead.Print.MpDetail"),
        TotalPointsLabel: Localization.Get("FinishRead.Print.TotalPoints"),
        ColSeq: Localization.Get("FinishRead.Print.ColSeq"),
        ColCode: Localization.Get("FinishRead.Print.ColCode"),
        ColElapsed: Localization.Get("FinishRead.Print.ColElapsed"),
        ColLeg: Localization.Get("FinishRead.Print.ColLeg"),
        ColDistance: Localization.Get("FinishRead.Print.ColDistance"),
        ColPace: Localization.Get("FinishRead.Print.ColPace"),
        ColPoints: Localization.Get("FinishRead.Print.ColPoints"),
        CorrectOrderTitle: Localization.Get("FinishRead.Print.CorrectOrder"));

    // A single-button (OK) message modal, reusing the confirm dialog for an info-only notice.
    private Task ShowInfoAsync(string messageKey) => _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
        Localization,
        titleKey: "Print.Settings.Title",
        messageKey: messageKey,
        confirmKey: "Common.Ok",
        cancelKey: "Common.Ok"));

    // --- Auto-read wiring --------------------------------------------------------------------------

    partial void OnAutoReadEnabledChanged(bool value)
    {
        if (value)
        {
            StartAutoRead();
            ShowActivity();
        }
        else
        {
            _poller.Stop();
            HideActivity();
            AutoReadError = string.Empty;
        }
    }

    partial void OnAutoReadFilePathChanged(string value)
    {
        if (AutoReadEnabled && !IsAutoReadPaused)
            StartAutoRead();
        UpdateActivityStatus();
    }

    partial void OnAutoReadIntervalSecondsChanged(int value)
    {
        if (AutoReadEnabled && !IsAutoReadPaused)
            StartAutoRead();
        UpdateActivityStatus();
    }

    [RelayCommand]
    private void IncrementInterval() => AutoReadIntervalSeconds++;

    [RelayCommand]
    private void DecrementInterval()
    {
        if (AutoReadIntervalSeconds > 1)
            AutoReadIntervalSeconds--;
    }

    private void StartAutoRead()
    {
        if (string.IsNullOrWhiteSpace(AutoReadFilePath))
        {
            _poller.Stop();
            return;
        }

        // Validate the file up front so the operator gets an immediate, specific message instead of a
        // silent no-op: a missing file usually means the timing software isn't exporting here (or the
        // wrong list format is selected), and an existing file the current parser can't read is the
        // wrong format. The poller creates the file when absent, so this check runs BEFORE it starts.
        _ = ValidateReadoutFileAsync();

        var interval = TimeSpan.FromSeconds(Math.Max(1, AutoReadIntervalSeconds));
        _poller.Start(AutoReadFilePath, interval, OnPolledContentAsync);
    }

    // Sets AutoReadError to a specific message when the watched file is missing or the current timing
    // system's parser can't read it, or clears it when the file looks right (or can't be examined yet).
    // Never throws. The "wrong format" message is per readout type (SPORTident points at the Config+ list
    // format; Sport Time is a generic mismatch).
    private async Task ValidateReadoutFileAsync()
    {
        var path = AutoReadFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            AutoReadError = string.Empty;
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                // No file at all — the timing software isn't exporting here. Point at the readout setup.
                AutoReadError = Localization.Get(NoFileErrorKey());
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path);
            var content = CsvEncodingReader.Decode(bytes);
            var parser = await _readoutParsers.GetCurrentAsync();
            // An empty file is the correct start state (the reader hasn't written yet) — not an error.
            if (!string.IsNullOrWhiteSpace(content) && !parser.CanParse(content))
            {
                AutoReadError = Localization.Get("FinishRead.AutoRead.Error.WrongFormat");
                return;
            }

            AutoReadError = string.Empty;
        }
        catch
        {
            // Locked/unreadable right now — don't nag; the poll loop will read it when it can.
            AutoReadError = string.Empty;
        }
    }

    // The "no file" message keyed on the selected timing system: SPORTident nudges the operator toward the
    // Config+ list format; Sport Time gives a generic "not exporting here" note.
    private string NoFileErrorKey() =>
        _readoutType == ReadoutType.SportTime
            ? "FinishRead.AutoRead.Error.NoFileSportTime"
            : "FinishRead.AutoRead.Error.NoFile";

    // Runs on a pool thread (the poller's loop). Parse + import are synchronous SQLite work, so they
    // stay off the UI thread; only the reload hops back. Silent: it just appends new reads.
    private async Task OnPolledContentAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || _session.CurrentDay is null)
            return;

        try
        {
            var parser = await _readoutParsers.GetCurrentAsync();
            var data = parser.Parse(content);
            // A good parse means the file is in the right format — clear any earlier warning banner.
            if (HasAutoReadError)
                await Dispatcher.UIThread.InvokeAsync(() => AutoReadError = string.Empty);

            var result = await _editor.ImportFinishReadoutsAsync(data);
            if (result.Added > 0)
            {
                await AutoPrintNewReadsAsync(result.AddedIds);
                await Dispatcher.UIThread.InvokeAsync(LoadAsync);
            }
        }
        catch (ReadoutFormatException)
        {
            // Not a readable readout right now (e.g. half-written) — skip this tick.
        }
    }

    // Silently prints each newly-read row when auto-print is on and a printer is configured. No-op when the
    // toggle is off, no printer is set (or it's gone), or printing isn't supported on this build — auto-read
    // must never block on a dialog or throw out of the poll loop. Runs on the poller's thread.
    private async Task AutoPrintNewReadsAsync(IReadOnlyList<Guid> readoutIds)
    {
        if (!AutoPrintEnabled || readoutIds.Count == 0 || !_printer.IsSupported)
            return;

        var settings = await _appSettings.GetPrintSettingsAsync();
        if (!settings.HasPrinter || !_printer.GetInstalledPrinters().Contains(settings.PrinterName))
            return;

        foreach (var id in readoutIds)
        {
            try
            {
                await PrintReadoutAsync(id, settings);
            }
            catch (PrintNotSupportedException)
            {
                return;
            }
        }
    }

    // Used on competition/day switch: turning the toggle off runs OnAutoReadEnabledChanged → Stop.
    private void StopAutoRead() => AutoReadEnabled = false;

    // --- Top-bar background activity ---------------------------------------------------------------

    private void ShowActivity()
    {
        IsAutoReadPaused = false;
        _activity = new FinishReadActivity(
            Localization,
            pause: PauseAutoRead,
            resume: ResumeAutoRead,
            stop: StopAutoRead,
            openSettings: () => NavigateToSelfRequested?.Invoke(this, EventArgs.Empty));
        UpdateActivityStatus();
        _activities.Register(_activity);
    }

    private void HideActivity()
    {
        IsAutoReadPaused = false;
        if (_activity is null)
            return;
        _activities.Unregister(_activity);
        _activity = null;
    }

    private void PauseAutoRead()
    {
        if (!AutoReadEnabled || IsAutoReadPaused)
            return;
        _poller.Stop();
        IsAutoReadPaused = true;
        UpdateActivityStatus();
    }

    private void ResumeAutoRead()
    {
        if (!AutoReadEnabled || !IsAutoReadPaused)
            return;
        IsAutoReadPaused = false;
        StartAutoRead();
        UpdateActivityStatus();
    }

    private void UpdateActivityStatus()
    {
        if (_activity is null)
            return;

        var fileName = string.IsNullOrWhiteSpace(AutoReadFilePath) ? "—" : Path.GetFileName(AutoReadFilePath);
        var key = IsAutoReadPaused ? "Activity.FinishRead.StatusPaused" : "Activity.FinishRead.Status";
        _activity.StatusText = string.Format(Localization.Get(key), fileName, AutoReadIntervalSeconds);
    }
}
