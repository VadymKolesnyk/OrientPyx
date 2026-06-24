using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// «Онлайн-результати» — publishes the CURRENT competition day's computed results to the online
/// (Supabase) service every N seconds as a background process, surfaced in the top-bar block like the
/// chip / finish auto-read. App-level connection keys come from Settings; this page edits the
/// per-competition publish options (slug / title / subtitle / standings / points) and starts/stops the
/// publish loop. The data published is OrientDesk's own computed results (the same the protocols show) —
/// the published day is the active session day.
/// </summary>
public sealed partial class OnlineResultsViewModel : PageViewModelBase
{
    private const int MaxLogLines = 200;

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IAppSettingsService _appSettings;
    private readonly IResultPublisher _publisher;
    private readonly IBackgroundActivityService _activities;
    private readonly IBusyService _busy;
    private readonly IActivityLog _log;

    // The running publish loop; null when stopped.
    private CancellationTokenSource? _publishCts;
    private Task? _publishTask;

    // Top-bar activity handle while publishing; null when off.
    private OnlineResultsActivity? _activity;

    // The app-level connection settings, loaded on Load and refreshed when publishing starts.
    private OnlineApiSettings _api = OnlineApiSettings.Empty;

    public OnlineResultsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IAppSettingsService appSettings,
        IResultPublisher publisher,
        IBackgroundActivityService activities,
        IBusyService busy,
        IActivityLog log)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _appSettings = appSettings;
        _publisher = publisher;
        _activities = activities;
        _busy = busy;
        _log = log;

        // Singleton VM: on a competition/day change, stop publishing (it belongs to the old selection) and
        // reload. SessionChanged may be raised on a pool thread, so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StopPublishing();
            _ = LoadAsync();
        });
    }

    public override string NavKey => "Nav.OnlineResults";
    public override string TitleKey => "Page.OnlineResults.Title";
    public override string TextKey => "Page.OnlineResults.Text";

    /// <summary>Raised when the user clicks "go to settings" on the top-bar activity — asks the shell to show this page.</summary>
    public event EventHandler? NavigateToSelfRequested;

    // --- Per-competition publish options (editable) ------------------------------------------------

    [ObservableProperty]
    private string _slug = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private bool _standings;

    [ObservableProperty]
    private bool _points;

    [ObservableProperty]
    private bool _settingsSaved;

    // --- Connection state (read-only here; edited in Settings) -------------------------------------

    /// <summary>True when the app-level Supabase URL + service-role key are configured.</summary>
    [ObservableProperty]
    private bool _isConnectionConfigured;

    /// <summary>The configured Supabase URL, shown read-only as a hint (blank → "налаштуйте в Settings").</summary>
    [ObservableProperty]
    private string _supabaseUrl = string.Empty;

    // --- Publish loop state ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private bool _isPublishing;

    [ObservableProperty]
    private bool _isPaused;

    public bool IsStopped => !IsPublishing;

    /// <summary>The spectator links generated for the configured competition (one per day, + «Сума»).</summary>
    public ObservableCollection<string> Links { get; } = [];

    /// <summary>The publish log, newest at the bottom.</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>Reloads the connection state, the per-competition options and the spectator links.</summary>
    public async Task LoadAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var (api, publish) = await _busy.RunAsync(async () =>
        {
            var a = await _appSettings.GetOnlineApiSettingsAsync();
            var p = await _editor.GetOnlinePublishSettingsAsync();
            return (a, p);
        });

        _api = api;
        SupabaseUrl = api.SupabaseUrl;
        IsConnectionConfigured = api.IsReadyToPublish;

        if (publish is not null)
        {
            Slug = publish.Slug;
            Title = publish.Title;
            Subtitle = publish.Subtitle;
            Standings = publish.Standings;
            Points = publish.Points;
        }

        RebuildLinks();
    }

    // Builds the shareable spectator links in the frontend's hash form: {base}/#/{slug}/d{n} per day,
    // plus a «Сума» link when standings are on. Empty when the base URL or slug isn't set.
    private void RebuildLinks()
    {
        Links.Clear();
        var baseUrl = _api.PublicBaseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(Slug))
            return;

        var dayCount = Math.Max(1, _session.CurrentEvent?.DayCount ?? 1);
        for (var d = 1; d <= dayCount; d++)
            Links.Add($"{baseUrl}/#/{Slug}/d{d}");
        if (Standings)
            Links.Add($"{baseUrl}/#/{Slug}/sum");
    }

    partial void OnSlugChanged(string value) => RebuildLinks();
    partial void OnStandingsChanged(bool value) => RebuildLinks();

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var settings = new OnlinePublishSettings(
            Slug.Trim(), Title.Trim(), Subtitle.Trim(), Standings, Points, Enabled: IsPublishing);
        await _busy.RunAsync(() => _editor.SaveOnlinePublishSettingsAsync(settings));

        // The metadata changed — make the next tick re-upload events/days/groups.
        _publisher.ResetMetadata();
        SettingsSaved = true;
        RebuildLinks();
    }

    // --- Start / stop publishing ------------------------------------------------------------------

    [RelayCommand]
    private async Task StartPublishingAsync()
    {
        if (IsPublishing || _session.CurrentEvent is null)
            return;

        // Re-read the connection settings (the user may have just filled them in on Settings).
        _api = await _appSettings.GetOnlineApiSettingsAsync();
        IsConnectionConfigured = _api.IsReadyToPublish;
        SupabaseUrl = _api.SupabaseUrl;
        if (!_api.IsReadyToPublish)
        {
            AppendLog(Localization.Get("OnlineResults.Log.NotConfigured"));
            return;
        }

        if (_session.CurrentDay is null)
        {
            AppendLog(Localization.Get("OnlineResults.Log.NoDay"));
            return;
        }

        IsPublishing = true;
        IsPaused = false;

        // Persist the current options + that publishing is enabled, and reset the publisher's metadata cache.
        await SaveSettingsAsync();

        ShowActivity();

        _publishCts = new CancellationTokenSource();
        _publishTask = Task.Run(() => PublishLoopAsync(_publishCts.Token));
        _log.Action("Online results: publishing started");
    }

    [RelayCommand]
    private void StopPublishing()
    {
        if (!IsPublishing)
            return;

        _publishCts?.Cancel();
        _publishCts = null;
        _publishTask = null;
        IsPublishing = false;
        IsPaused = false;
        HideActivity();
        AppendLog(Localization.Get("OnlineResults.Log.Stopped"));
        _log.Action("Online results: publishing stopped");

        // Record that publishing is no longer enabled for this competition.
        _ = _editor.SaveOnlinePublishSettingsAsync(new OnlinePublishSettings(
            Slug.Trim(), Title.Trim(), Subtitle.Trim(), Standings, Points, Enabled: false));
    }

    // The publish loop — runs on a pool thread. Each tick builds a snapshot for the active day and pushes it.
    // Building the snapshot is SQLite work (off the UI thread already); only log/UI writes hop back.
    private async Task PublishLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(OnlineApiSettings.MinIntervalSeconds, _api.IntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            if (!IsPaused)
            {
                try
                {
                    await PublishOnceAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppendLog(string.Format(Localization.Get("OnlineResults.Log.Error"), ex.Message));
                }
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task PublishOnceAsync(CancellationToken ct)
    {
        var day = _session.CurrentDay;
        if (day is null)
            return;

        var publish = await _editor.GetOnlinePublishSettingsAsync(ct);
        if (publish is null)
            return;

        var snapshot = await _editor.GetOnlineResultsSnapshotAsync(day.Id, ct);
        await _publisher.PublishAsync(publish, _api, snapshot, ct);

        var finished = snapshot.Rows.Count(r => r.Place is not null);
        AppendLog(string.Format(
            Localization.Get("OnlineResults.Log.Tick"),
            day.Number, snapshot.Rows.Count, finished));
        UpdateActivityStatus();
    }

    // --- Top-bar background activity ---------------------------------------------------------------

    private void ShowActivity()
    {
        IsPaused = false;
        _activity = new OnlineResultsActivity(
            Localization,
            pause: PausePublishing,
            resume: ResumePublishing,
            stop: StopPublishing,
            openSettings: () => NavigateToSelfRequested?.Invoke(this, EventArgs.Empty));
        UpdateActivityStatus();
        _activities.Register(_activity);
    }

    private void HideActivity()
    {
        IsPaused = false;
        if (_activity is null)
            return;
        _activities.Unregister(_activity);
        _activity = null;
    }

    private void PausePublishing()
    {
        if (!IsPublishing || IsPaused)
            return;
        IsPaused = true;
        UpdateActivityStatus();
    }

    private void ResumePublishing()
    {
        if (!IsPublishing || !IsPaused)
            return;
        IsPaused = false;
        UpdateActivityStatus();
    }

    private void UpdateActivityStatus()
    {
        if (_activity is null)
            return;

        var key = IsPaused ? "Activity.OnlineResults.StatusPaused" : "Activity.OnlineResults.Status";
        _activity.StatusText = string.Format(Localization.Get(key), Slug, _api.IntervalSeconds);
    }

    // Appends a timestamped line to the log, trimming the oldest when it grows too long. Safe from a pool
    // thread (the publish loop calls it): it marshals onto the UI thread, since LogLines backs the UI.
    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (Dispatcher.UIThread.CheckAccess())
            AppendLogCore(line);
        else
            Dispatcher.UIThread.Post(() => AppendLogCore(line));
    }

    private void AppendLogCore(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }
}
