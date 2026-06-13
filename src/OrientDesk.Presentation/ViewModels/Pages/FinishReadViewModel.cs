using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.BusinessLogic.Services;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
    private readonly IReadoutParser _readoutParser;
    private readonly IFileReadoutPoller _poller;
    private readonly IBusyService _busy;
    private readonly IBackgroundActivityService _activities;

    /// <summary>Top-bar activity handle while auto-read runs; null when off.</summary>
    private FinishReadActivity? _activity;

    public FinishReadViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IReadoutParser readoutParser,
        IFileReadoutPoller poller,
        IBusyService busy,
        IBackgroundActivityService activities)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _readoutParser = readoutParser;
        _poller = poller;
        _busy = busy;
        _activities = activities;

        // Singleton VM: on a competition/day change, stop the watch (the file belongs to the old day)
        // and reload. SessionChanged may be raised on a pool thread, so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StopAutoRead();
            _ = LoadAsync();
        });
    }

    public override string NavKey => "Nav.FinishRead";
    public override string TitleKey => "Page.FinishRead.Title";
    public override string TextKey => "Page.FinishRead.Text";

    /// <summary>Raised when the user clicks "go to settings" on the top-bar activity — asks the shell to show this page.</summary>
    public event EventHandler? NavigateToSelfRequested;

    /// <summary>The day's read log, newest at the bottom (ordered by sequence). Read-only rows.</summary>
    public ObservableCollection<FinishReadRowViewModel> Readouts { get; } = [];

    /// <summary>Selectable days for the top-right day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

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

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    [RelayCommand]
    private void ToggleAutoRead() => IsAutoReadExpanded = !IsAutoReadExpanded;

    /// <summary>Reloads the day picker and the current day's log. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        var hasDay = _session.CurrentDay is not null;
        var (days, rows) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var r = hasDay ? await _editor.GetFinishReadoutRowsAsync() : (IReadOnlyList<FinishReadoutRow>)[];
            return (d, r);
        });

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

        Readouts.Clear();
        foreach (var row in rows)
            Readouts.Add(new FinishReadRowViewModel(row, Localization));
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

        var interval = TimeSpan.FromSeconds(Math.Max(1, AutoReadIntervalSeconds));
        _poller.Start(AutoReadFilePath, interval, OnPolledContentAsync);
    }

    // Runs on a pool thread (the poller's loop). Parse + import are synchronous SQLite work, so they
    // stay off the UI thread; only the reload hops back. Silent: it just appends new reads.
    private async Task OnPolledContentAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || _session.CurrentDay is null)
            return;

        try
        {
            var data = _readoutParser.Parse(content);
            var result = await _editor.ImportFinishReadoutsAsync(data);
            if (result.Added > 0)
                await Dispatcher.UIThread.InvokeAsync(LoadAsync);
        }
        catch (ReadoutFormatException)
        {
            // Not a readable readout right now (e.g. half-written) — skip this tick.
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
