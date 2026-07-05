using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Shared;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Онлайн-результати» — publishes the CURRENT competition day's computed results to the online
/// (Supabase) service every N seconds as a background process, surfaced in the top-bar block like the
/// chip / finish auto-read. App-level connection keys come from Settings; this page edits the
/// per-competition publish options (slug / title / subtitle / standings / points) and starts/stops the
/// publish loop. The data published is OrientPyx's own computed results (the same the protocols show) —
/// the published day is the active session day.
/// </summary>
public sealed partial class OnlineResultsViewModel : PageViewModelBase
{
    private const int MaxLogLines = 200;

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IAppSettingsService _appSettings;
    private readonly IAppStore _appStore;
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
        IAppStore appStore,
        IResultPublisher publisher,
        IBackgroundActivityService activities,
        IBusyService busy,
        IActivityLog log)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _appSettings = appSettings;
        _appStore = appStore;
        _publisher = publisher;
        _activities = activities;
        _busy = busy;
        _log = log;

        Columns = new OnlineColumnsEditorViewModel(localization);
        Columns.Changed += (_, _) =>
        {
            UpdatePointsColumnVisible();
            RequestPreviewRefresh();
            ScheduleAutoSave();
        };

        // Singleton VM: on a competition/day change, stop publishing (it belongs to the old selection) and
        // reload. SessionChanged may be raised on a pool thread, so marshal onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StopPublishing();
            _previewSource = null; // the day's cached snapshot no longer applies
            _previewSourceDay = Guid.Empty;
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

    /// <summary>True when the «Очки» column is enabled (large or small screen) in the column editor. Gates the
    /// points-rule status/warning below — there's no longer a separate "show points" toggle: the column's own
    /// visibility check-boxes decide whether it appears.</summary>
    [ObservableProperty]
    private bool _pointsColumnVisible;

    /// <summary>The editor for which result columns the spectator frontend displays, each with its large /
    /// small-screen visibility (sent as display_config).</summary>
    public OnlineColumnsEditorViewModel Columns { get; }

    /// <summary>A live mock-up of the spectator results table for the active day — reflects the column order +
    /// large-screen set + a per-column phone-hidden badge; its header cells are the drag-reorder surface.</summary>
    public OnlinePreviewViewModel Preview { get; } = new();

    // --- «Очки» rule status (shown next to the "show points column" checkbox) ----------------------

    /// <summary>True when at least one points rule applies to the active day (a competition default or a
    /// per-group override). When false, the published «Очки» column will be empty — surfaced as a warning.</summary>
    [ObservableProperty]
    private bool _hasPointsRule;

    /// <summary>The warning shown when NO rule applies — «жодній групі не призначено правило…».</summary>
    [ObservableProperty]
    private string _pointsWarning = string.Empty;

    /// <summary>When a rule DOES apply: a line naming the effective default rule and its formula/table.</summary>
    [ObservableProperty]
    private string _pointsFormulaInfo = string.Empty;

    /// <summary>When some groups deviate from the default (own rule or explicit "no points"): a line listing
    /// those exception groups. Empty when every group uses the default.</summary>
    [ObservableProperty]
    private string _pointsExceptionsInfo = string.Empty;

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

    /// <summary>The whole log as one block of text, so the View can show it in a single selectable/copyable
    /// control (drag-select across lines). Refreshed whenever <see cref="LogLines"/> changes.</summary>
    public string LogText => string.Join(Environment.NewLine, LogLines);

    /// <summary>Reloads the connection state, the per-competition options and the spectator links.</summary>
    public async Task LoadAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var (api, publish, rules, info, groups) = await _busy.RunAsync(async () =>
        {
            var a = await _appSettings.GetOnlineApiSettingsAsync();
            var p = await _editor.GetOnlinePublishSettingsAsync();
            var r = await _appStore.GetPointsRulesAsync();
            var i = await _editor.GetInfoAsync();
            // The published day is the active session day; describe its groups (null when no day).
            var g = _session.CurrentDay is null
                ? (IReadOnlyList<GroupDayRow>)[]
                : await _editor.GetGroupDayRowsAsync();
            return (a, p, r, i, g);
        });

        _api = api;
        SupabaseUrl = api.SupabaseUrl;
        IsConnectionConfigured = api.IsReadyToPublish;

        _loading = true; // filling the fields must not trigger auto-save
        try
        {
            if (publish is not null)
            {
                Slug = publish.Slug;
                Title = publish.Title;
                Subtitle = publish.Subtitle;
                Standings = publish.Standings;
                Columns.Load(publish.EffectiveDisplay);
            }
        }
        finally
        {
            _loading = false;
        }

        UpdatePointsColumnVisible();
        UpdatePointsRuleStatus(rules, info?.DefaultPointsRuleId, groups);
        RebuildLinks();
        RequestPreviewRefresh(immediate: true);
    }

    // Recomputes whether the «Очки» column is enabled on either screen, so the points-rule status below the
    // options is shown only when the column will actually appear.
    private void UpdatePointsColumnVisible() =>
        PointsColumnVisible = Columns.Columns.Any(c => c.Key == "points" && (c.Lg || c.Sm));

    // Describes the «Очки» situation for the active day, for the info/warning beside the "show points
    // column" checkbox. The effective rule per group is its own PointsRuleId override, else the competition
    // default; Guid.Empty means an explicit "no points". When NOTHING applies we warn the column will be
    // empty; otherwise we name the default rule + its formula/table and list any deviating (exception) groups.
    private void UpdatePointsRuleStatus(
        IReadOnlyList<PointsRule> rules, Guid? defaultRuleId, IReadOnlyList<GroupDayRow> groups)
    {
        PointsWarning = string.Empty;
        PointsFormulaInfo = string.Empty;
        PointsExceptionsInfo = string.Empty;

        var byId = rules.ToDictionary(r => r.Id);
        PointsRule? Effective(Guid? id) =>
            id is { } gid && gid != Guid.Empty && byId.TryGetValue(gid, out var rule) ? rule : null;

        var defaultRule = Effective(defaultRuleId);

        // A group earns points when its effective rule (override, else default) resolves to a real rule.
        bool GroupScores(GroupDayRow g) =>
            g.PointsRuleId is { } over
                ? over != Guid.Empty && byId.ContainsKey(over)
                : defaultRule is not null;

        HasPointsRule = defaultRule is not null || groups.Any(GroupScores);

        if (!HasPointsRule)
        {
            PointsWarning = Localization.Get("OnlineResults.Points.Warning");
            return;
        }

        // The headline formula/table line. When there's a default, it names that; when only some groups
        // have a rule (no default), it says points come from per-group rules instead.
        PointsFormulaInfo = defaultRule is not null
            ? string.Format(Localization.Get("OnlineResults.Points.Formula"),
                defaultRule.Name, DescribeRule(defaultRule))
            : Localization.Get("OnlineResults.Points.PerGroupOnly");

        // Exception groups: those whose effective rule differs from the default (an explicit other rule,
        // or an explicit "no points"). Only meaningful when a default exists.
        if (defaultRule is not null)
        {
            var exceptions = new List<string>();
            foreach (var g in groups)
            {
                if (g.PointsRuleId is not { } over)
                    continue; // inherits the default — not an exception
                var label = over == Guid.Empty
                    ? Localization.Get("OnlineResults.Points.NoPoints")
                    : byId.TryGetValue(over, out var rule) ? rule.Name : Localization.Get("OnlineResults.Points.NoPoints");
                exceptions.Add($"{g.Name} — {label}");
            }

            if (exceptions.Count > 0)
                PointsExceptionsInfo = string.Format(
                    Localization.Get("OnlineResults.Points.Exceptions"),
                    string.Join("; ", exceptions));
        }
    }

    // A short human description of how a rule scores: the formula text, or "таблиця місць" for a table.
    private string DescribeRule(PointsRule rule) =>
        rule.Kind == PointsRuleKind.Formula && !string.IsNullOrWhiteSpace(rule.Formula)
            ? rule.Formula!
            : Localization.Get("OnlineResults.Points.TableKind");

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

    partial void OnSlugChanged(string value)
    {
        RebuildLinks();
        ScheduleAutoSave();
    }

    partial void OnStandingsChanged(bool value)
    {
        RebuildLinks();
        ScheduleAutoSave();
    }

    // The preview mirrors the published header; keep it in step as the user edits the title/subtitle.
    partial void OnTitleChanged(string value)
    {
        Preview.Title = value;
        ScheduleAutoSave();
    }

    partial void OnSubtitleChanged(string value)
    {
        Preview.Subtitle = value;
        ScheduleAutoSave();
    }

    // --- Auto-save --------------------------------------------------------------------------------
    // The publish options persist automatically whenever the user edits them — there's no Save button. A burst
    // of edits (typing, dragging) is debounced into ONE write, and the write itself runs on a pool thread (no
    // busy overlay), so the UI never blocks. LoadAsync sets a guard so filling the fields doesn't self-save.

    private const int AutoSaveDebounceMs = 600;
    private CancellationTokenSource? _autoSaveCts;
    private bool _loading;

    private void ScheduleAutoSave()
    {
        if (_loading || _session.CurrentEvent is null)
            return;

        _autoSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        _ = AutoSaveAsync(cts.Token);
    }

    private async Task AutoSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(AutoSaveDebounceMs, ct); // collapse a burst of edits into one write
            await PersistAsync(ct);
            RebuildLinks();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit — ignore.
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localization.Get("OnlineResults.Log.Error"), ex.Message));
        }
    }

    // Persists the current options (with the given enabled flag) off the UI thread and resets the publisher's
    // metadata cache so the next tick re-uploads events/days/groups.
    private async Task PersistAsync(CancellationToken ct = default, bool? enabled = null)
    {
        if (_session.CurrentEvent is null)
            return;

        var settings = new OnlinePublishSettings(
            // Points is always enabled: whether the «Очки» column appears is now decided purely by its own
            // large/small-screen visibility in the column config (display_config), not a separate toggle.
            Slug.Trim(), Title.Trim(), Subtitle.Trim(), Standings, Points: true, Enabled: enabled ?? IsPublishing,
            Columns: null, Display: Columns.BuildConfig());
        await Task.Run(() => _editor.SaveOnlinePublishSettingsAsync(settings, ct), ct);
        _publisher.ResetMetadata();
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
        _lastSkippedNoNumber = -1; // a fresh run should warn again about un-numbered participants

        // Persist the current options + that publishing is enabled, and reset the publisher's metadata cache.
        // Drop any pending debounced auto-save so it can't overwrite Enabled with the pre-start value.
        _autoSaveCts?.Cancel();
        await PersistAsync(enabled: true);

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

        _autoSaveCts?.Cancel(); // drop any pending auto-save; we write Enabled:false explicitly below
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
            Slug.Trim(), Title.Trim(), Subtitle.Trim(), Standings, Points: true, Enabled: false,
            Columns: null, Display: Columns.BuildConfig()));
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

        // Warn (once, until it changes) that some participants are left out because they have no start
        // number — the frontend can't address an un-numbered runner, so they won't appear online.
        if (snapshot.SkippedNoNumber != _lastSkippedNoNumber)
        {
            _lastSkippedNoNumber = snapshot.SkippedNoNumber;
            if (snapshot.SkippedNoNumber > 0)
                AppendLog(string.Format(
                    Localization.Get("OnlineResults.Log.SkippedNoNumber"), snapshot.SkippedNoNumber));
        }

        UpdateActivityStatus();
    }

    // The last skipped-no-number count we warned about, so the warning is logged only when it changes
    // (not on every tick). Reset when publishing (re)starts so a fresh run warns again.
    private int _lastSkippedNoNumber = -1;

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

    // --- Live spectator preview -------------------------------------------------------------------

    // The active day's computed snapshot the preview builds from (the SAME data the publisher sends), loaded
    // ONCE per day and cached so a column/toggle change re-renders the preview without a DB read. Cleared on a
    // session/day change (see the SessionChanged handler in the ctor).
    private OnlineResultsSnapshot? _previewSource;
    private Guid _previewSourceDay;

    // Debounce: a burst of toggles (or dragging) coalesces into ONE preview rebuild once the user pauses,
    // instead of rebuilding the preview grid on every change. Each request restarts the timer.
    private const int PreviewDebounceMs = 180;
    private CancellationTokenSource? _previewDebounceCts;

    /// <summary>Moves a column next to another in the editor's list (drag-reorder from the preview header). Keys
    /// are the stable <see cref="ResultColumnDef.Key"/>. The editor's Changed then re-renders the preview.</summary>
    public void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter)
    {
        var dragged = Columns.Columns.FirstOrDefault(c => c.Key == draggedKey);
        var target = Columns.Columns.FirstOrDefault(c => c.Key == targetKey);
        Columns.MoveColumn(dragged, target, insertAfter);
    }

    private void RequestPreviewRefresh(bool immediate = false)
    {
        _previewDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewDebounceCts = cts;
        _ = RefreshPreviewAsync(immediate ? 0 : PreviewDebounceMs, cts.Token);
    }

    // Re-renders the preview for the active day, debounced. The snapshot load is the only DB hit (off the UI
    // thread, once per day); the layout/format work is cheap; the visual apply is on the UI thread.
    private async Task RefreshPreviewAsync(int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, ct);

            if (_session.CurrentEvent is null || _session.CurrentDay is not { } day)
            {
                ApplyPreview(null);
                return;
            }

            if (_previewSource is null || _previewSourceDay != day.Id)
            {
                var source = await Task.Run(() => _editor.GetOnlineResultsSnapshotAsync(day.Id, ct), ct);
                ct.ThrowIfCancellationRequested();
                _previewSource = source;
                _previewSourceDay = day.Id;
            }

            ApplyPreview(_previewSource);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request — ignore.
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localization.Get("OnlineResults.Log.Error"), ex.Message));
        }
    }

    // The preview is a MOCK-UP, not the whole page — cap the total body rows so the visual tree stays small.
    private const int PreviewTotalRowCap = 10;
    private const int PreviewPerGroupCap = 10;

    // Fills Preview from the day's snapshot + the editor's current column layout: the large-screen columns in
    // order (small-screen-hidden ones badged), and each group's rows (placed finishers first) formatted per
    // column. A column shown only on the small screen is still drawn (so it's visible for reordering) but always
    // carries the phone badge concept via ShownOnSmall.
    private void ApplyPreview(OnlineResultsSnapshot? snapshot)
    {
        Preview.Columns.Clear();
        Preview.Sections.Clear();

        // The columns the preview draws = everything visible on at least one screen, in the editor's order.
        var visible = Columns.Columns.Where(c => c.Lg || c.Sm).ToList();

        if (snapshot is null || !snapshot.HasData || visible.Count == 0)
        {
            Preview.Title = string.Empty;
            Preview.Subtitle = string.Empty;
            Preview.IsEmpty = true;
            Preview.RaiseChanged();
            return;
        }

        Preview.Title = Title;
        Preview.Subtitle = Subtitle;

        foreach (var c in visible)
            Preview.Columns.Add(new OnlinePreviewColumn(c.Key, c.Label, c.Column, ShownOnSmall: c.Sm));

        // Group the day's rows by group, in the snapshot's group order.
        var byGroup = snapshot.Rows
            .GroupBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var remaining = PreviewTotalRowCap;
        foreach (var group in snapshot.Groups)
        {
            if (remaining <= 0)
                break;
            if (!byGroup.TryGetValue(group.Name, out var rows) || rows.Count == 0)
                continue;

            // Leader's clean time for the «Відставання» column.
            var leader = rows
                .Where(r => r.Place is not null && r.ResultTime is not null && !r.OutOfCompetition)
                .OrderBy(r => r.ResultTime!.Value)
                .Select(r => r.ResultTime)
                .FirstOrDefault();

            var ordered = rows
                .OrderBy(r => r.Place ?? int.MaxValue)
                .ThenBy(r => r.ResultTime ?? TimeSpan.MaxValue)
                .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var take = Math.Min(Math.Min(PreviewPerGroupCap, remaining), ordered.Count);
            var previewRows = new List<OnlinePreviewRow>(take);
            for (var i = 0; i < take; i++)
                previewRows.Add(BuildPreviewRow(ordered[i], visible, leader));
            remaining -= take;

            var facts = BuildGroupFacts(group);
            Preview.Sections.Add(new OnlinePreviewSection(group.Name, facts, previewRows));
        }

        Preview.IsEmpty = Preview.Sections.Count == 0 || Preview.Sections.All(s => s.Rows.Count == 0);
        Preview.RaiseChanged();
    }

    private static string BuildGroupFacts(OnlineGroup g)
    {
        var parts = new List<string>(2);
        if (g.ControlCount is { } cc && cc > 0)
            parts.Add($"{cc} КП");
        if (g.DistanceKm is { } km && km > 0)
            parts.Add($"{km.ToString("0.#", CultureInfo.InvariantCulture)} км");
        return string.Join("  ·  ", parts);
    }

    // Formats one result row into the visible columns, parallel to the column list — mirrors the values the
    // spectator frontend renders from the published fields.
    private OnlinePreviewRow BuildPreviewRow(OnlineResultRow r, IReadOnlyList<OnlineColumnItem> columns, TimeSpan? leader)
    {
        var placed = r.Place is not null && !r.OutOfCompetition;
        var values = new List<string>(columns.Count);

        foreach (var col in columns)
            values.Add(col.Column switch
            {
                ResultColumn.Place => r.OutOfCompetition ? "П/К" : r.Place?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ResultColumn.FullName => r.FullName,
                ResultColumn.Bib => r.Bib?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ResultColumn.Birth => r.Birth,
                ResultColumn.Qual => r.Qual,
                ResultColumn.Team => r.Team,
                ResultColumn.Club => r.Club,
                ResultColumn.Region => r.Region,
                ResultColumn.StartTime => FormatClock(r.StartTime),
                ResultColumn.ResultTime => r.Status == FinishStatus.Ok ? FormatSpan(r.ResultTime) : string.Empty,
                ResultColumn.Gap => placed && leader is { } l && r.ResultTime is { } rt && rt > l
                    ? "+" + FormatSpan(rt - l)
                    : string.Empty,
                ResultColumn.Status => PreviewStatusText(r),
                ResultColumn.Points => r.Points?.ToString("0.##", CultureInfo.InvariantCulture)
                    ?? r.Score?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                _ => string.Empty,
            });

        return new OnlinePreviewRow(values, Unplaced: r.Place is null || r.OutOfCompetition);
    }

    // The short status shown for an unplaced run; blank for a clean finish (its time is in the result column).
    private static string PreviewStatusText(OnlineResultRow r) => r.Status switch
    {
        FinishStatus.Dns => "DNS",
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dsq => "DSQ",
        FinishStatus.Ok => string.Empty,
        _ => r.HasReadout ? string.Empty : "на дистанції",
    };

    private static string FormatSpan(TimeSpan? t) =>
        t is { } v ? (v.TotalHours >= 1
            ? v.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : v.ToString(@"m\:ss", CultureInfo.InvariantCulture))
        : string.Empty;

    private static string FormatClock(TimeSpan? t) =>
        t is { } v ? v.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty;

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
        OnPropertyChanged(nameof(LogText));
    }
}
