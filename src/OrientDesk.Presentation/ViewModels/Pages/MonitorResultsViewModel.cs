using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Shared;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// «Результати на монітор» — generates self-contained, auto-refreshing / auto-scrolling HTML result screens
/// for venue monitors. The user defines a set of output files, each mapping a chosen subset of the active
/// day's groups to one .html file with its own column layout and timing. A background loop regenerates every
/// enabled file each tick from the day's computed results (the same data the protocols / online publish use),
/// surfaced in the top-bar block like the online publish. Settings are per-competition (event.db); the day
/// generated is the active session day.
/// </summary>
public sealed partial class MonitorResultsViewModel : PageViewModelBase
{
    private const int MaxLogLines = 200;
    private const int TickSeconds = 5; // how often the loop checks whether any file is due to be rewritten

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IMonitorHtmlWriter _writer;
    private readonly IBackgroundActivityService _activities;
    private readonly IBusyService _busy;
    private readonly IActivityLog _log;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private MonitorResultsActivity? _activity;

    public MonitorResultsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IMonitorHtmlWriter writer,
        IBackgroundActivityService activities,
        IBusyService busy,
        IActivityLog log)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _writer = writer;
        _activities = activities;
        _busy = busy;
        _log = log;

        // One shared column layout for ALL files (order + visibility). Editing it marks the settings dirty and
        // re-renders the selected file's preview; the reorder-by-drag on any preview header also moves it here.
        Columns = new ResultColumnsEditorViewModel(localization);
        Columns.Changed += (_, _) =>
        {
            SettingsSaved = false;
            if (SelectedFile is { } f)
                RequestPreviewRefresh(f);
        };

        // Singleton VM: on a competition/day change, stop generating, drop the cached preview snapshot (it's
        // per-day), and reload for the new selection.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StopGenerating();
            _previewSource = null;
            _ = LoadAsync();
        });
    }

    /// <summary>The column layout shared by every output file (order + show/hide). Editing here or dragging a
    /// preview header changes what all rezNN pages show.</summary>
    public ResultColumnsEditorViewModel Columns { get; }

    public override string NavKey => "Nav.Monitor";
    public override string TitleKey => "Page.Monitor.Title";
    public override string TextKey => "Page.Monitor.Text";

    /// <summary>Raised when "go to settings" is clicked on the top-bar activity — asks the shell to show this page.</summary>
    public event EventHandler? NavigateToSelfRequested;

    /// <summary>The configured output files.</summary>
    public ObservableCollection<MonitorFileViewModel> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private MonitorFileViewModel? _selectedFile;

    public bool HasSelectedFile => SelectedFile is not null;

    /// <summary>The active day's group names, offered to each file as toggles.</summary>
    public ObservableCollection<string> AvailableGroups { get; } = [];

    /// <summary>Selectable days for the top-right day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _isPaused;

    public bool IsStopped => !IsGenerating;

    [ObservableProperty]
    private bool _settingsSaved;

    /// <summary>True when no day is selected — generation needs the active session day.</summary>
    [ObservableProperty]
    private bool _hasDay;

    public ObservableCollection<string> LogLines { get; } = [];
    public string LogText => string.Join(Environment.NewLine, LogLines);

    public async Task LoadAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var (settings, days, groups) = await _busy.RunAsync(async () =>
        {
            var s = await _editor.GetMonitorSettingsAsync();
            var d = await _editor.GetDaysAsync();
            var g = _session.CurrentDay is null
                ? (IReadOnlyList<GroupDayRow>)[]
                : await _editor.GetGroupDayRowsAsync();
            return (s, d, g);
        });

        HasDay = _session.CurrentDay is not null;

        // Day picker — same pattern as the other per-day pages: switching the day re-points the session.
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

        AvailableGroups.Clear();
        foreach (var g in groups.OrderBy(g => g.Name, StringComparer.CurrentCulture))
            AvailableGroups.Add(g.Name);

        // The shared column layout for all files (seeded from the first legacy per-file layout for old configs).
        Columns.Load((settings ?? MonitorSettings.Empty).EffectiveColumns);

        Files.Clear();
        foreach (var f in settings?.Files ?? [])
            Files.Add(CreateFileVm(f));

        SelectedFile = Files.FirstOrDefault();
    }

    // True when the current options already represent exactly these days (same count and numbers, in order).
    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        if (DayOptions.Count != days.Count)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i].Number != days[i].Number)
                return false;
        return true;
    }

    // Driven by the day ComboBox. Switching the session's day re-raises SessionChanged, which reloads this
    // page (and stops generation); the _syncingDay guard stops LoadAsync's reassignment from re-entering.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
            return;

        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    private MonitorFileViewModel CreateFileVm(MonitorFile file)
    {
        var vm = new MonitorFileViewModel(Localization, file, AvailableGroups, Columns);
        vm.Changed += (_, _) => SettingsSaved = false;
        // A group toggle on this file re-renders its live preview (only when it's the one on screen — the
        // others rebuild when next selected). Column changes are shared and handled on the Columns editor.
        vm.PreviewRefreshRequested += (s, e) =>
        {
            if (ReferenceEquals(s, SelectedFile))
                RequestPreviewRefresh((MonitorFileViewModel)s!);
        };
        return vm;
    }

    // The day's computed result snapshot the preview builds from, loaded ONCE per day and cached here so a
    // column/group toggle re-renders the preview without a DB read. Invalidated (cleared) on a day/session change.
    private MonitorPreviewSource? _previewSource;
    private Guid _previewSourceDay;

    // Debounce: a burst of toggles (e.g. «Позначити всі», or fast clicking) coalesces into ONE preview rebuild
    // once the user pauses, instead of rebuilding the (large) preview grid on every single change — that
    // per-change rebuild on the UI thread was the lag. Each request restarts the timer; the pending build is
    // also cancelled if it's superseded.
    private const int PreviewDebounceMs = 180;
    private CancellationTokenSource? _previewDebounceCts;

    // Re-render the preview for the selected file, debounced. The snapshot load is the only DB hit (off the UI
    // thread, once per day); the document build runs off-thread; only the final visual apply is on the UI thread.
    partial void OnSelectedFileChanged(MonitorFileViewModel? value)
    {
        // Switching files should feel immediate — no debounce delay, just refresh.
        if (value is not null)
            RequestPreviewRefresh(value, immediate: true);
    }

    private void RequestPreviewRefresh(MonitorFileViewModel file, bool immediate = false)
    {
        _previewDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewDebounceCts = cts;
        _ = RefreshPreviewAsync(file, immediate ? 0 : PreviewDebounceMs, cts.Token);
    }

    private async Task RefreshPreviewAsync(MonitorFileViewModel file, int delayMs, CancellationToken ct)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, ct); // collapse a burst of edits into one rebuild

            if (_session.CurrentEvent is null || _session.CurrentDay is not { } day)
            {
                file.ApplyPreview(null);
                return;
            }

            // Load + cache the day's snapshot the first time (or after a day change), off the UI thread.
            if (_previewSource is null || _previewSourceDay != day.Id)
            {
                var source = await Task.Run(() => _editor.GetMonitorPreviewSourceAsync(day.Id), ct);
                ct.ThrowIfCancellationRequested();
                _previewSource = source;
                _previewSourceDay = day.Id;
            }

            if (_previewSource is null || !ReferenceEquals(file, SelectedFile))
            {
                if (_previewSource is null)
                    file.ApplyPreview(null);
                return;
            }

            // Build the document off the UI thread (it formats every shown row); snapshot the file's current
            // layout first so the background build reads a stable copy.
            var model = file.ToModel();
            var source2 = _previewSource;
            var columns = Columns.BuildSelection(); // shared across all files
            var labels = BuildLabels();
            var document = await Task.Run(() => _editor.BuildMonitorPreview(model, columns, source2, labels), ct);
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(file, SelectedFile))
                return; // selection changed while building

            file.ApplyPreview(document); // resumes on the UI thread — the only UI-thread work
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request — ignore.
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(Localization.Get("Monitor.Log.Error"), ex.Message));
        }
    }

    [RelayCommand]
    private void AddFile()
    {
        // Suggest the next free rezNN.html file name (files all live in the competition's monitor folder).
        var index = Files.Count + 1;
        var name = $"rez{index:00}.html";
        var title = string.Format(Localization.Get("Monitor.File.DefaultTitle"), index);
        var vm = CreateFileVm(MonitorFile.New(name, title));
        Files.Add(vm);
        SelectedFile = vm;
        SettingsSaved = false;
    }

    /// <summary>Opens a monitor file in the OS default browser. Saves settings first (so the current file name
    /// is on disk), generates the file once if it doesn't exist yet, then launches it. No-op without a day.</summary>
    [RelayCommand]
    private async Task OpenFileAsync(MonitorFileViewModel? file)
    {
        if (file is null || _session.CurrentEvent is null)
            return;

        if (_session.CurrentDay is null)
        {
            AppendLog(Localization.Get("Monitor.Log.NoDay"));
            return;
        }

        var fullPath = _editor.ResolveMonitorFilePath(file.Path);
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        // Make sure the file exists: persist the latest names and generate this one if needed.
        await SaveSettingsAsync();
        if (!File.Exists(fullPath))
            await GenerateOneAsync(file, CancellationToken.None);

        OpenInBrowser(fullPath);
    }

    // Builds and writes a single file once (used when opening a file that hasn't been generated yet).
    private async Task GenerateOneAsync(MonitorFileViewModel file, CancellationToken ct)
    {
        var day = _session.CurrentDay;
        if (day is null)
            return;

        var documents = await _busy.RunAsync(() => _editor.BuildMonitorDocumentsAsync(day.Id, BuildLabels(), ct));
        var name = Path.GetFileName(file.Path);
        var doc = documents.FirstOrDefault(d =>
            string.Equals(Path.GetFileName(d.Path), name, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
            return;

        var bytes = _writer.Write(doc.Document);
        await WriteMonitorFileAsync(doc.Path, bytes, ct);
    }

    private void OpenInBrowser(string fullPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch
        {
            // No handler / file removed / sandbox — opening is best-effort, so swallow it.
        }
    }

    [RelayCommand]
    private void RemoveFile(MonitorFileViewModel? file)
    {
        if (file is null)
            return;
        Files.Remove(file);
        if (SelectedFile == file)
            SelectedFile = Files.FirstOrDefault();
        SettingsSaved = false;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var settings = new MonitorSettings(Files.Select(f => f.ToModel()).ToList(), Columns.BuildSelection());
        await _busy.RunAsync(() => _editor.SaveMonitorSettingsAsync(settings));
        SettingsSaved = true;
    }

    // --- Start / stop generation -------------------------------------------------------------------

    [RelayCommand]
    private async Task StartGeneratingAsync()
    {
        if (IsGenerating || _session.CurrentEvent is null)
            return;

        if (_session.CurrentDay is null)
        {
            AppendLog(Localization.Get("Monitor.Log.NoDay"));
            return;
        }

        await SaveSettingsAsync();

        if (Files.All(f => !f.Enabled || string.IsNullOrWhiteSpace(f.Path)))
        {
            AppendLog(Localization.Get("Monitor.Log.NoFiles"));
            return;
        }

        IsGenerating = true;
        IsPaused = false;
        ShowActivity();

        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => GenerateLoopAsync(_runCts.Token));
        _log.Action("Monitor results: generation started");
    }

    [RelayCommand]
    private void StopGenerating()
    {
        if (!IsGenerating)
            return;

        _runCts?.Cancel();
        _runCts = null;
        _runTask = null;
        IsGenerating = false;
        IsPaused = false;
        HideActivity();
        AppendLog(Localization.Get("Monitor.Log.Stopped"));
        _log.Action("Monitor results: generation stopped");
    }

    // The generation loop — runs on a pool thread. Each file is rewritten on its own interval; the loop wakes
    // every few seconds and writes any file whose interval has elapsed (and on the very first pass).
    private async Task GenerateLoopAsync(CancellationToken ct)
    {
        var nextDue = new Dictionary<string, DateTime>();
        while (!ct.IsCancellationRequested)
        {
            if (!IsPaused)
            {
                try
                {
                    await GenerateDueAsync(nextDue, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppendLog(string.Format(Localization.Get("Monitor.Log.Error"), ex.Message));
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task GenerateDueAsync(Dictionary<string, DateTime> nextDue, CancellationToken ct)
    {
        var day = _session.CurrentDay;
        if (day is null)
            return;

        var documents = await _editor.BuildMonitorDocumentsAsync(day.Id, BuildLabels(), ct);
        var now = DateTime.UtcNow;

        foreach (var fileDoc in documents)
        {
            if (nextDue.TryGetValue(fileDoc.Path, out var due) && now < due)
                continue; // not yet time to rewrite this file

            var bytes = _writer.Write(fileDoc.Document);
            await WriteMonitorFileAsync(fileDoc.Path, bytes, ct);
            nextDue[fileDoc.Path] = now.AddSeconds(Math.Max(MonitorFile.MinRefreshSeconds, fileDoc.Document.RefreshSeconds));

            var groupCount = fileDoc.Document.Groups.Count;
            var rowCount = fileDoc.Document.Groups.Sum(g => g.Cells.Count);
            AppendLog(string.Format(
                Localization.Get("Monitor.Log.Wrote"),
                Path.GetFileName(fileDoc.Path), groupCount, rowCount));
        }

        UpdateActivityStatus(documents.Count);
    }

    // Writes a generated monitor file to its location (the built day's folder, events/<id>/day{N}) — the screen
    // lives with the day whose results it shows. The path is resolved by BuildMonitorDocumentsAsync.
    private static async Task WriteMonitorFileAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    // Gathers the localized labels the editor service needs to format the documents (it stays free of
    // ILocalizationService). Column headers come from the shared column catalogue.
    private MonitorLabels BuildLabels()
    {
        var headers = ResultColumnDef.All.ToDictionary(
            d => d.Column, d => Localization.Get(d.LabelKey));
        return new MonitorLabels(
            ColumnHeaders: headers,
            DistanceLabel: Localization.Get("Monitor.Caption.Distance"),
            ControlCountLabel: Localization.Get("Monitor.Caption.Controls"),
            GeneratedLabel: Localization.Get("Monitor.Caption.Generated"),
            StatusDns: "DNS",
            StatusMp: "MP",
            StatusOvt: "OVT",
            StatusDnf: "DNF",
            StatusDsq: "DSQ",
            StatusRunning: Localization.Get("Monitor.Status.Running"));
    }

    // --- Top-bar background activity ---------------------------------------------------------------

    private void ShowActivity()
    {
        IsPaused = false;
        _activity = new MonitorResultsActivity(
            Localization,
            pause: PauseGenerating,
            resume: ResumeGenerating,
            stop: StopGenerating,
            openSettings: () => NavigateToSelfRequested?.Invoke(this, EventArgs.Empty));
        UpdateActivityStatus(Files.Count(f => f.Enabled));
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

    private void PauseGenerating()
    {
        if (!IsGenerating || IsPaused)
            return;
        IsPaused = true;
        UpdateActivityStatus(Files.Count(f => f.Enabled));
    }

    private void ResumeGenerating()
    {
        if (!IsGenerating || !IsPaused)
            return;
        IsPaused = false;
        UpdateActivityStatus(Files.Count(f => f.Enabled));
    }

    private void UpdateActivityStatus(int fileCount)
    {
        if (_activity is null)
            return;
        var key = IsPaused ? "Activity.Monitor.StatusPaused" : "Activity.Monitor.Status";
        _activity.StatusText = string.Format(Localization.Get(key), fileCount);
    }

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
