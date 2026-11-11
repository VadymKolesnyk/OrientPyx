using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Команди» (relay teams, Р-20…Р-26) for the current day: a table of the day's teams (number, name, group, club,
/// region, «П/К», status ruling, computed status/time/place) and, for the selected team, its legs with the runner
/// on each. A runner is picked from a layout-tolerant search over the competition's participants and put on a
/// leg; a runner who does not run the day yet is added to it in the team's group. «Присвоєння номерів» numbers the
/// shown teams in their on-screen order, like the participants page.
/// </summary>
public sealed partial class RelayTeamsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IActivityLog _log;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();
    private readonly PendingSaves _pendingSaves = new();
    private IReadOnlyList<RelayRunner> _runners = [];
    private bool _syncingDay;

    public RelayTeamsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock,
        IDataChangeBus bus,
        IWorkspaceMode mode,
        IActivityLog log)
        : base(localization)
    {
        UseDayLock(dayLock);
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;
        _log = log;
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
        // Live data (another station's writes) — see ReloadAsync.
        ObserveData(bus, session, mode,
            DataArea.Relay, DataArea.ParticipantDays, DataArea.Participants, DataArea.Groups, DataArea.GroupDaySettings, DataArea.Clubs, DataArea.Regions, DataArea.Days);
    }

    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.RelayTeams";
    public override string TitleKey => "Page.RelayTeams.Title";
    public override string TextKey => "Page.RelayTeams.Text";

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    public ObservableCollection<RelayTeamRowViewModel> Teams { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTeam))]
    private RelayTeamRowViewModel? _selectedTeam;

    public bool HasSelectedTeam => SelectedTeam is not null;

    /// <summary>The legs of the selected team.</summary>
    public ObservableCollection<RelayLegRowViewModel> Legs { get; } = [];

    /// <summary>The selected team's runners without a leg («без етапу» — to be put on a leg or taken off the team).</summary>
    public ObservableCollection<RelayUnplacedRunner> Unplaced { get; } = [];

    public bool HasUnplaced => Unplaced.Count > 0;

    /// <summary>What on the day still needs a hand: runners without a leg, teams without a number; blank when nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTeamsNotice))]
    private string _teamsNotice = string.Empty;

    public bool HasTeamsNotice => TeamsNotice.Length > 0;

    /// <summary>The day has runners without a leg — the notice reads as a warning then.</summary>
    [ObservableProperty]
    private bool _teamsNoticeIsWarning;

    /// <summary>The day's relay groups (the team group combo).</summary>
    public ObservableCollection<RelayGroupOption> GroupOptions { get; } = [];

    public bool HasRelayGroups => GroupOptions.Count > 0;

    /// <summary>Some relay group of the day gives its teams a course set (Р-41) — the «Набір дистанцій» column shows.</summary>
    public bool ShowCourseSetColumn { get; private set; }

    /// <summary>Raised when the set of team columns may have changed; the view rebuilds its bands.</summary>
    public event EventHandler? ColumnsChanged;

    public IReadOnlyList<ClubOption> ClubOptions { get; private set; } = [];

    public IReadOnlyList<RegionOption> RegionOptions { get; private set; } = [];

    // ── Runner search for the legs panel
    [ObservableProperty]
    private string _runnerQuery = string.Empty;

    public ObservableCollection<RelayRunner> RunnerMatches { get; } = [];

    [ObservableProperty]
    private RelayRunner? _selectedRunner;

    /// <inheritdoc />
    /// <remarks>The teams (and the selected team's legs) are merged in place, so the selection survives and only the
    /// cells that changed repaint; the pending saves just have to finish first.</remarks>
    protected override async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _pendingSaves.WaitAsync();
        await LoadCoreAsync(merge: true);
    }

    public async Task LoadAsync()
    {
        CancelAllTimers();
        await LoadCoreAsync(merge: false);
    }

    // Reads the day and either rebuilds the tables (first open / session switch) or merges them in place.
    private async Task LoadCoreAsync(bool merge)
    {
        var hasDay = _session.CurrentDay is not null;
        var (days, data, clubs, regions) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var t = hasDay ? await _editor.GetRelayTeamsDataAsync() : new RelayTeamsData([], [], []);
            var c = await _editor.GetClubsAsync();
            var r = await _editor.GetRegionsAsync();
            return (d, t, c, r);
        });

        _syncingDay = true;
        try
        {
            // Keep the DayOption instances while the day set is the same, so the picker's SelectedItem stays valid.
            if (DayOptions.Count != days.Count || DayOptions.Where((o, i) => o.Number != days[i].Number).Any())
            {
                DayOptions.Clear();
                foreach (var day in days)
                    DayOptions.Add(new DayOption(day, Localization));
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
        RefreshDayLock();

        // The shared option lists are rebuilt only when their content moved, so a row's combo keeps its items
        // (and its selected instance) across a live refresh.
        if (!merge || !ClubOptions.Skip(1).Select(o => (o.Id, o.Label)).SequenceEqual(clubs.Select(c => ((Guid?)c.Id, c.Name))))
        {
            ClubOptions = [ClubOption.None(Localization), .. clubs.Select(c => new ClubOption(c.Id, c.Name, Localization))];
            OnPropertyChanged(nameof(ClubOptions));
        }
        if (!merge || !RegionOptions.Skip(1).Select(o => (o.Id, o.Label)).SequenceEqual(regions.Select(r => ((Guid?)r.Id, r.Name))))
        {
            RegionOptions = [RegionOption.None(Localization), .. regions.Select(r => new RegionOption(r.Id, r.Name, Localization))];
            OnPropertyChanged(nameof(RegionOptions));
        }

        var groupsChanged = !merge || !GroupOptions
            .Select(g => (g.GroupId, g.Label, g.LegCount, g.CourseMode, g.TeamCourseSet))
            .SequenceEqual(data.Groups.Select(g => (g.GroupId, g.GroupName, g.LegCount, g.CourseMode, g.TeamCourseSet)));
        if (groupsChanged)
        {
            GroupOptions.Clear();
            foreach (var g in data.Groups)
                GroupOptions.Add(new RelayGroupOption(g.GroupId, g.GroupName, g.LegCount, g.CourseMode, g.TeamCourseSet));
            OnPropertyChanged(nameof(HasRelayGroups));
        }
        var showSet = GroupOptions.Any(g => g.CourseMode == RelayCourseMode.TeamSet);
        if (showSet != ShowCourseSetColumn)
        {
            ShowCourseSetColumn = showSet;
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }

        var previous = SelectedTeam;
        if (!merge)
            Teams.Clear();
        RowSync.Sync(Teams, data.Teams,
            itemKey: team => team.Id,
            rowKey: row => row.Id,
            create: team => new RelayTeamRowViewModel(team, Localization, GroupOptions, ClubOptions, RegionOptions, QueueSave),
            update: (row, team) => row.Apply(team, ClubOptions, RegionOptions));
        _runners = data.Runners;
        RefreshTeamsNotice(data.Teams);

        // The same selected team keeps its object, so the selection setter does not re-run: merge its legs here.
        var selected = previous is not null && Teams.Contains(previous) ? previous : Teams.FirstOrDefault();
        if (ReferenceEquals(selected, SelectedTeam))
            RebuildLegs(merge: true);
        else
            SelectedTeam = selected;
        RefreshRunnerMatches();
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null || _session.CurrentDay?.Number == value.Number)
            return;
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    partial void OnSelectedTeamChanged(RelayTeamRowViewModel? value) => RebuildLegs(merge: false);

    // merge: keep the leg rows (matched by leg number) and change only what moved — a live refresh of the team
    // being shown; otherwise the panel is rebuilt for a newly selected team.
    // «Учасників без етапу: N (команд: M)» / «Команд без номера: K».
    private void RefreshTeamsNotice(IReadOnlyList<RelayTeamRow> teams)
    {
        var parts = new List<string>(2);
        var unplaced = teams.Sum(t => t.Unplaced.Count);
        if (unplaced > 0)
            parts.Add(string.Format(Localization.Get("RelayTeams.Notice.Unplaced"), unplaced, teams.Count(t => t.Unplaced.Count > 0)));
        var unnumbered = teams.Count(t => t.Number is null);
        if (unnumbered > 0)
            parts.Add(string.Format(Localization.Get("RelayTeams.Notice.Unnumbered"), unnumbered));
        TeamsNoticeIsWarning = unplaced > 0;
        TeamsNotice = string.Join(" ", parts);
    }

    private void RebuildLegs(bool merge)
    {
        RebuildUnplaced();
        if (SelectedTeam is not { } team)
        {
            Legs.Clear();
            return;
        }
        if (!merge)
            Legs.Clear();
        var mode = GroupOptions.FirstOrDefault(g => g.GroupId == team.Model.GroupId)?.CourseMode ?? RelayCourseMode.Group;
        RowSync.Sync(Legs, team.Model.Legs,
            itemKey: leg => leg.Leg,
            rowKey: row => row.Leg,
            create: leg => new RelayLegRowViewModel(team.Model, leg, Localization, mode, QueueLegCourseSave),
            update: (row, leg) => row.Apply(team.Model, leg, mode));
    }

    // The selected team's runners without a leg; the list is replaced only when it changed.
    private void RebuildUnplaced()
    {
        var fresh = SelectedTeam?.Model.Unplaced ?? [];
        if (fresh.SequenceEqual(Unplaced))
            return;
        Unplaced.Clear();
        foreach (var runner in fresh)
            Unplaced.Add(runner);
        OnPropertyChanged(nameof(HasUnplaced));
    }

    /// <summary>«Вибрати»: a runner without a leg becomes the picked runner, so «Призначити» puts them on a leg.</summary>
    [RelayCommand]
    private void PickUnplaced(RelayUnplacedRunner? runner)
    {
        if (runner is null)
            return;
        RunnerQuery = runner.FullName;
        SelectedRunner = RunnerMatches.FirstOrDefault(r => r.ParticipantId == runner.ParticipantId);
    }

    /// <summary>«Прибрати з команди»: the runner without a leg leaves the team (they stay on the day in their group).</summary>
    [RelayCommand]
    private async Task ReleaseUnplacedAsync(RelayUnplacedRunner? runner)
    {
        if (runner is null || _session.CurrentDay is not { } day || !await EnsureDayEditableAsync())
            return;
        await _busy.RunAsync(() => _editor.PlaceRelayRunnerAsync(
            new RelayPlacementRequest(runner.ParticipantId, day.Id, TeamNumber: null, Leg: null)));
        await LoadAsync();
    }

    // «За номером етапу» (Р-41): a typed leg course is saved after a short pause.
    private void QueueLegCourseSave(RelayLegRowViewModel leg)
    {
        if (IsDayLocked || leg.IsApplying || leg.TeamNumber is not { } teamNumber)
            return;
        var key = leg.SaveKey;
        CancelTimer(key);
        var cts = new CancellationTokenSource();
        _saveTimers[key] = cts;
        _pendingSaves.Track(SaveAsync());

        async Task SaveAsync()
        {
            try
            {
                await Task.Delay(SaveDebounce, cts.Token);
                var entry = new LegCourseEntry(teamNumber, leg.Leg, leg.BibCourseText ?? string.Empty);
                await Task.Run(() => _editor.SetLegCoursesAsync([entry], cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (DayLockedException)
            {
            }
        }
    }

    /// <summary>
    /// Imports leg-number courses (IOF <c>TeamCourseAssignment</c> or «100.2;B» lines) from a file's text the view
    /// read, then reloads; reports how many were stored.
    /// </summary>
    public async Task ImportLegCoursesAsync(string content)
    {
        if (!await EnsureDayEditableAsync())
            return;
        int count;
        try
        {
            count = await _busy.RunAsync(() => _editor.ImportLegCoursesAsync(content));
        }
        catch (FormatException)
        {
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(Localization,
                titleKey: "RelayTeams.ImportCourses.Title", messageKey: "RelayTeams.ImportCourses.Invalid",
                confirmKey: "Common.Ok") { IsMessage = true });
            return;
        }
        await LoadAsync();
        await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(Localization,
            titleKey: "RelayTeams.ImportCourses.Title", messageKey: "RelayTeams.ImportCourses.Done",
            confirmKey: "Common.Ok") { IsMessage = true, MessageArgs = [count] });
    }

    partial void OnRunnerQueryChanged(string value) => RefreshRunnerMatches();

    // Layout-tolerant search over every participant (name, number); at most 50 matches.
    private void RefreshRunnerMatches()
    {
        RunnerMatches.Clear();
        var query = (RunnerQuery ?? string.Empty).Trim();
        if (query.Length == 0)
            return;
        var variants = TextSearch.Variants(query);
        foreach (var runner in _runners
                     .Where(r => TextSearch.ContainsAny(r.FullName, variants) || TextSearch.ContainsAny(r.Number, variants))
                     .Take(50))
            RunnerMatches.Add(runner);
    }

    /// <summary>
    /// «Додати команду»: one team in the selected team's group (else the day's first relay group), numbered one above
    /// the day's highest number; the new team gets selected, so the next one lands in the same group.
    /// </summary>
    [RelayCommand]
    private async Task AddTeamAsync()
    {
        var group = GroupOptions.FirstOrDefault(g => g.GroupId == SelectedTeam?.Model.GroupId) ?? GroupOptions.FirstOrDefault();
        if (group is null || !await EnsureDayEditableAsync())
            return;
        await _pendingSaves.WaitAsync();
        var before = Teams.Select(t => t.Id).ToHashSet();
        var next = (Teams.Select(t => t.Model.Number).Max() ?? 0) + 1;
        await _busy.RunAsync(() => _editor.CreateRelayTeamsAsync(group.GroupId, 1, next));
        await LoadCoreAsync(merge: true);
        if (Teams.FirstOrDefault(t => !before.Contains(t.Id)) is { } created)
            SelectedTeam = created;
    }

    /// <summary>
    /// «Присвоєння номерів» (as on the participants page): numbers the teams the table shows
    /// (<paramref name="visibleRows"/> — filtered and sorted) from a start number, in their on-screen order. Without
    /// «Переприсвоювати» a team with a number keeps it and the numbers other teams of the day hold are stepped over;
    /// with it every shown team is renumbered and a number another team holds is taken from it first. Numbers are
    /// unique per day (Р-20); the whole set is written at once.
    /// </summary>
    [RelayCommand]
    private async Task AssignNumbersAsync(IReadOnlyList<object?>? visibleRows)
    {
        // The order as the user saw it: the table's list is live and the refresh below may re-sort it.
        var shown = visibleRows?.OfType<RelayTeamRowViewModel>().ToList() ?? [];
        if (shown.Count == 0 || !await EnsureDayEditableAsync())
            return;
        // Typed numbers land first and the rows are read afresh, so the numbers held are the stored ones.
        await _pendingSaves.WaitAsync();
        await LoadCoreAsync(merge: true);
        var rows = shown.Where(Teams.Contains).ToList();
        if (rows.Count == 0)
            return;

        var numbers = Teams.ToDictionary(t => t.Id, t => t.Model.Number);
        var holders = new Dictionary<int, Guid>();
        foreach (var (id, number) in numbers)
            if (number is { } n)
                holders[n] = id;
        var result = await _dialogs.ShowAssignNumbersAsync(new AssignNumbersViewModel(
            Localization, holders.Count == 0 ? 1 : holders.Keys.Max() + 1, "RelayTeams.AssignNumbers.Title"));
        if (result is null)
        {
            RequestGridFocus();
            return;
        }

        var original = new Dictionary<Guid, int?>(numbers);
        var assigned = new List<(int Number, Guid Team)>();
        var cleared = new List<(int Number, Guid Team)>();
        var next = result.StartNumber;
        foreach (var row in rows)
        {
            if (!result.ReassignExisting)
            {
                if (numbers[row.Id] is not null)
                    continue;
                while (holders.ContainsKey(next))
                    next++;
            }
            else if (holders.TryGetValue(next, out var other) && other != row.Id)
            {
                numbers[other] = null;
                cleared.Add((next, other));
            }
            if (numbers[row.Id] is { } old && holders.GetValueOrDefault(old) == row.Id)
                holders.Remove(old);
            numbers[row.Id] = next;
            holders[next] = row.Id;
            assigned.Add((next, row.Id));
            next++;
        }

        var changes = numbers.Where(kv => kv.Value != original[kv.Key])
            .Select(kv => new RelayTeamNumberChange(kv.Key, kv.Value))
            .ToList();
        if (changes.Count == 0)
        {
            _log.Action(Localization.Get("RelayTeams.AssignNumbers.Log.None"));
            RequestGridFocus();
            return;
        }

        var labels = Teams.ToDictionary(t => t.Id, LogLabel);
        var saved = await _busy.RunAsync(() => _editor.SetRelayTeamNumbersAsync(changes));
        await LoadCoreAsync(merge: true);
        if (!saved)
        {
            // Another station numbered a team meanwhile — nothing was written; the table shows the numbers as they are.
            await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(Localization,
                titleKey: "RelayTeams.AssignNumbers.Title", messageKey: "RelayTeams.AssignNumbers.Conflict",
                confirmKey: "Common.Ok") { IsMessage = true });
            RequestGridFocus();
            return;
        }
        LogNumberAssignment(assigned, cleared, labels);
        RequestGridFocus();
    }

    // «Естафета-1 (Ж21)» — how the activity log names a team (it may have no name).
    private static string LogLabel(RelayTeamRowViewModel row)
    {
        var name = string.IsNullOrWhiteSpace(row.Model.Name) ? "—" : row.Model.Name.Trim();
        return row.SelectedGroup is { } group ? $"{name} ({group.Label})" : name;
    }

    private void LogNumberAssignment(
        IReadOnlyList<(int Number, Guid Team)> assigned, IReadOnlyList<(int Number, Guid Team)> cleared,
        IReadOnlyDictionary<Guid, string> labels)
    {
        _log.Action(string.Format(CultureInfo.CurrentCulture, Localization.Get("RelayTeams.AssignNumbers.Log.Assigned"), assigned.Count));
        foreach (var (number, team) in assigned)
            _log.Action(string.Format(CultureInfo.CurrentCulture,
                Localization.Get("Participants.AssignNumbers.Log.AssignedRow"), number, labels.GetValueOrDefault(team, "—")));
        if (cleared.Count == 0)
            return;
        _log.Action(string.Format(CultureInfo.CurrentCulture, Localization.Get("RelayTeams.AssignNumbers.Log.Cleared"), cleared.Count));
        foreach (var (number, team) in cleared)
            _log.Action(string.Format(CultureInfo.CurrentCulture,
                Localization.Get("Participants.AssignNumbers.Log.ClearedRow"), number, labels.GetValueOrDefault(team, "—")));
    }

    [RelayCommand]
    private Task DeleteTeamAsync(RelayTeamRowViewModel? row) => RemoveTeamAsync(row, skipConfirm: false);

    public Task DeleteTeamNoConfirmAsync(RelayTeamRowViewModel? row) => RemoveTeamAsync(row, skipConfirm: true);

    public Task DeleteSelectedTeamAsync(bool skipConfirm) => RemoveTeamAsync(SelectedTeam, skipConfirm);

    private async Task RemoveTeamAsync(RelayTeamRowViewModel? row, bool skipConfirm)
    {
        if (row is null || !await EnsureDayEditableAsync())
            return;
        if (!skipConfirm && !await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization, titleKey: "RelayTeams.Delete.ConfirmTitle", messageKey: "RelayTeams.Delete.ConfirmMessage")))
        {
            RequestGridFocus();
            return;
        }
        CancelTimer(row.Id);
        await _busy.RunAsync(() => _editor.DeleteRelayTeamAsync(row.Id));
        await LoadAsync();
        RequestGridFocus();
    }

    /// <summary>Puts the runner picked in the search list on <paramref name="leg"/> of the selected team.</summary>
    [RelayCommand]
    private async Task AssignLegAsync(RelayLegRowViewModel? leg)
    {
        if (leg is null || SelectedRunner is not { } runner || !await EnsureDayEditableAsync())
            return;
        await _busy.RunAsync(() => _editor.AssignRelayLegAsync(leg.TeamId, leg.Leg, runner.ParticipantId));
        RunnerQuery = string.Empty;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ClearLegAsync(RelayLegRowViewModel? leg)
    {
        if (leg?.ParticipantId is null || !await EnsureDayEditableAsync())
            return;
        await _busy.RunAsync(() => _editor.AssignRelayLegAsync(leg.TeamId, leg.Leg, null));
        await LoadAsync();
    }

    // A team edit: debounced per row; a taken number is refused (Р-20) and the row shows the stored one again.
    private void QueueSave(RelayTeamRowViewModel row)
    {
        if (IsDayLocked || row.IsApplying)
            return;
        CancelTimer(row.Id);
        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _pendingSaves.Track(SaveAsync());

        async Task SaveAsync()
        {
            try
            {
                await Task.Delay(SaveDebounce, cts.Token);
                if (row.ToEdit() is not { } edit)
                    return;
                var saved = await Task.Run(() => _editor.UpdateRelayTeamAsync(edit, cts.Token), cts.Token);
                if (!saved)
                    row.NumberError = true;
                else
                {
                    row.NumberError = false;
                    if (edit.GroupId != row.Model.GroupId)
                        await LoadAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (DayLockedException)
            {
            }
        }
    }

    private void CancelTimer(Guid id)
    {
        if (_saveTimers.Remove(id, out var cts))
            cts.Cancel();
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}

/// <summary>A relay group of the day (combo option).</summary>
public sealed class RelayGroupOption(
    Guid groupId, string name, int legCount, RelayCourseMode courseMode = RelayCourseMode.Group, string teamCourseSet = "")
{
    public Guid GroupId { get; } = groupId;

    public string Label { get; } = name;

    public int LegCount { get; } = legCount;

    /// <summary>How the group's legs get their courses (Р-41).</summary>
    public RelayCourseMode CourseMode { get; } = courseMode;

    /// <summary>The group's default course set («A B») for <see cref="RelayCourseMode.TeamSet"/>.</summary>
    public string TeamCourseSet { get; } = teamCourseSet;

    public override string ToString() => Label;
}

/// <summary>One editable team row of the «Команди» table.</summary>
public sealed partial class RelayTeamRowViewModel : ObservableObject
{
    private readonly Action<RelayTeamRowViewModel> _requestSave;
    private bool _initialized;

    public RelayTeamRowViewModel(
        RelayTeamRow model, ILocalizationService localization,
        IReadOnlyList<RelayGroupOption> groups, IReadOnlyList<ClubOption> clubs, IReadOnlyList<RegionOption> regions,
        Action<RelayTeamRowViewModel> requestSave)
    {
        Model = model;
        Localization = localization;
        _requestSave = requestSave;
        GroupOptions = groups;
        _clubOptions = clubs;
        _regionOptions = regions;
        _numberText = RelayTeamNumbers.Text(model.Number);
        _name = model.Name;
        _selectedGroup = groups.FirstOrDefault(g => g.GroupId == model.GroupId) ?? groups.FirstOrDefault();
        _selectedClub = clubs.FirstOrDefault(c => c.Id == model.ClubId) ?? clubs[0];
        _selectedRegion = regions.FirstOrDefault(r => r.Id == model.RegionId) ?? regions[0];
        _outOfCompetition = model.OutOfCompetition;
        _courseSetText = model.CourseSet;
        _statusOptions = FinishStatusOptions.Build(localization, model.Result?.Computed ?? FinishStatus.None);
        _selectedStatus = FinishStatusOptions.Select(StatusOptions, model.StatusOverride);
        _initialized = true;
    }

    public RelayTeamRow Model { get; private set; }

    public ILocalizationService Localization { get; }

    public Guid Id => Model.Id;

    public IReadOnlyList<RelayGroupOption> GroupOptions { get; }

    [ObservableProperty]
    private IReadOnlyList<ClubOption> _clubOptions;

    [ObservableProperty]
    private IReadOnlyList<RegionOption> _regionOptions;

    [ObservableProperty]
    private IReadOnlyList<FinishStatusOption> _statusOptions;

    /// <summary>True while <see cref="Apply"/> copies fresh values onto the row; the page skips its save callback then.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>
    /// Copies a freshly-read team onto this row without triggering a save. Only the cells whose value changed raise
    /// a property change; the shared option lists are swapped only when the page rebuilt them.
    /// </summary>
    public void Apply(RelayTeamRow model, IReadOnlyList<ClubOption> clubs, IReadOnlyList<RegionOption> regions)
    {
        IsApplying = true;
        _initialized = false;
        try
        {
            var previous = Model;
            Model = model;
            if (!ReferenceEquals(ClubOptions, clubs))
                ClubOptions = clubs;
            if (!ReferenceEquals(RegionOptions, regions))
                RegionOptions = regions;

            NumberText = RelayTeamNumbers.Text(model.Number);
            Name = model.Name;
            SelectedGroup = GroupOptions.FirstOrDefault(g => g.GroupId == model.GroupId) ?? GroupOptions.FirstOrDefault();
            SelectedClub = ClubOptions.FirstOrDefault(c => c.Id == model.ClubId) ?? ClubOptions[0];
            SelectedRegion = RegionOptions.FirstOrDefault(r => r.Id == model.RegionId) ?? RegionOptions[0];
            OutOfCompetition = model.OutOfCompetition;
            CourseSetText = model.CourseSet;
            NumberError = false;

            // The "(… — автоматично)" sentinel names the computed status, so it is rebuilt when that moved.
            var computed = model.Result?.Computed ?? FinishStatus.None;
            if ((previous.Result?.Computed ?? FinishStatus.None) != computed)
                StatusOptions = FinishStatusOptions.Build(Localization, computed);
            SelectedStatus = FinishStatusOptions.Select(StatusOptions, model.StatusOverride);

            OnPropertyChanged(nameof(LegsText));
            OnPropertyChanged(nameof(HasUnplaced));
            OnPropertyChanged(nameof(CourseSetPlaceholder));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(PlaceText));
            OnPropertyChanged(nameof(PlaceSort));
        }
        finally
        {
            _initialized = true;
            IsApplying = false;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberSort))]
    private string _numberText;

    /// <summary>Sorts by number, the teams without one last.</summary>
    public int NumberSort => int.TryParse(NumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private RelayGroupOption? _selectedGroup;

    [ObservableProperty]
    private ClubOption _selectedClub;

    [ObservableProperty]
    private RegionOption _selectedRegion;

    [ObservableProperty]
    private bool _outOfCompetition;

    [ObservableProperty]
    private FinishStatusOption _selectedStatus;

    /// <summary>«Набір на команду» (Р-41): the team's own course set («A B»); blank = the group's.</summary>
    [ObservableProperty]
    private string _courseSetText;

    /// <summary>The set a blank team set falls back to, shown as its placeholder: the courses assigned to the team's
    /// leg numbers (OCAD), else the group's set.</summary>
    public string CourseSetPlaceholder =>
        Model.AssignedCourseSet is { Length: > 0 } assigned ? assigned
        : SelectedGroup?.TeamCourseSet is { Length: > 0 } set ? set
        : "—";

    /// <summary>The typed number is taken by another team of the day (Р-20).</summary>
    [ObservableProperty]
    private bool _numberError;

    /// <summary>«2/3»: assigned legs of the leg count; «4/4 · без етапу: 2» when runners have no leg.</summary>
    public string LegsText
    {
        get
        {
            var legs = $"{Model.Legs.Count(l => l.LinkId is not null)}/{Model.Legs.Count}";
            return HasUnplaced
                ? string.Format(Localization.Get("RelayTeams.LegsWithUnplaced"), legs, Model.Unplaced.Count)
                : legs;
        }
    }

    /// <summary>The team has runners without a leg — the «Етапи» cell is tinted red until fixed.</summary>
    public bool HasUnplaced => Model.Unplaced.Count > 0;

    public string StatusText => Model.Result is { } r ? RelayText.TeamStatus(r, Localization) : string.Empty;

    public string TimeText => Model.Result?.Time is { } t ? RelayText.Time(t) : string.Empty;

    public string PlaceText => Model.Result is { } r
        ? r.Place?.ToString(CultureInfo.InvariantCulture)
          ?? (r.OutOfCompetition ? ParticipantDayResult.OutOfCompetitionMark : string.Empty)
        : string.Empty;

    public int PlaceSort => Model.Result?.Place ?? int.MaxValue;

    /// <summary>The row as an edit; null when the typed number is not a number (a blank one leaves the team without a
    /// number — numbers can be given later).</summary>
    public RelayTeamEdit? ToEdit()
    {
        int? number = null;
        var text = (NumberText ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
                return null;
            number = n;
        }
        if (SelectedGroup is null)
            return null;
        return new RelayTeamEdit(Id, SelectedGroup.GroupId, number, Name ?? string.Empty,
            SelectedClub.Id, SelectedRegion.Id, OutOfCompetition, SelectedStatus.Status, CourseSetText ?? string.Empty);
    }

    partial void OnNumberTextChanged(string value) => QueueSave();
    partial void OnNameChanged(string value) => QueueSave();
    partial void OnSelectedGroupChanged(RelayGroupOption? value)
    {
        OnPropertyChanged(nameof(CourseSetPlaceholder));
        QueueSave();
    }
    partial void OnSelectedClubChanged(ClubOption value) => QueueSave();
    partial void OnSelectedRegionChanged(RegionOption value) => QueueSave();
    partial void OnOutOfCompetitionChanged(bool value) => QueueSave();
    partial void OnSelectedStatusChanged(FinishStatusOption value) => QueueSave();
    partial void OnCourseSetTextChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}

/// <summary>One leg of the selected team in the legs panel.</summary>
public sealed partial class RelayLegRowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Action<RelayLegRowViewModel> _saveBibCourse;
    private RelayTeamRow _team;
    private RelayLegRow _leg;
    private RelayCourseMode _courseMode;

    public RelayLegRowViewModel(
        RelayTeamRow team, RelayLegRow leg, ILocalizationService localization, RelayCourseMode courseMode,
        Action<RelayLegRowViewModel> saveBibCourse)
    {
        _team = team;
        _leg = leg;
        _localization = localization;
        _courseMode = courseMode;
        _saveBibCourse = saveBibCourse;
        Leg = leg.Leg;
        _bibCourseText = leg.BibCourse;
    }

    public Guid TeamId => _team.Id;

    public int? TeamNumber => _team.Number;

    public int Leg { get; }

    /// <summary>A stable key for this leg's debounced course save.</summary>
    public Guid SaveKey => new(_team.Id.ToByteArray().Select((b, i) => i == 15 ? (byte)(b ^ Leg) : b).ToArray());

    /// <summary>«За номером етапу» (Р-41): the leg number's course is typed here — once the team has a number.</summary>
    public bool IsBibCourseEditable => _courseMode == RelayCourseMode.ByBib && _team.Number is not null;

    [ObservableProperty]
    private string _bibCourseText;

    /// <summary>True while <see cref="Apply"/> copies fresh values onto the row; the page skips its save callback then.</summary>
    public bool IsApplying { get; private set; }

    partial void OnBibCourseTextChanged(string value) => _saveBibCourse(this);

    /// <summary>The leg's course (Р-44): «дистанція B», «дистанцію не призначено» or — in a team set before the
    /// leg is read — «не визначено» (Р-43); blank when the group runs one course.</summary>
    public string CourseText => _courseMode == RelayCourseMode.Group
        ? string.Empty
        : _leg.Result?.CourseCode is { Length: > 0 } code
            ? string.Format(_localization.Get("Relay.CourseOf"), code)
            : _leg.Result is { CourseUnassigned: true }
                ? _localization.Get("Relay.NoCourse")
                : _courseMode == RelayCourseMode.TeamSet
                    ? _localization.Get("Relay.CourseUndetermined")
                    : string.Empty;

    public bool HasCourseText => CourseText.Length > 0;

    public Guid? ParticipantId => _leg.ParticipantId;

    /// <summary>«Етап 2 · 100.2»; «Етап 2» while the team has no number.</summary>
    public string Header => _team.Number is { } number
        ? string.Format(_localization.Get("RelayTeams.Leg.Header"), Leg, RelayBib.Format(number, Leg))
        : string.Format(_localization.Get("RelayTeams.Leg.HeaderNoNumber"), Leg);

    public string Runner => _leg.LinkId is null ? _localization.Get("RelayTeams.Leg.Empty") : _leg.FullName;

    public bool HasRunner => ParticipantId is not null;

    public string Chip => _leg.Chip;

    /// <summary>The leg's computed state: status · time · cumulative, «очікує», «перезапуск».</summary>
    public string ResultText => _leg.Result is { } r ? RelayText.Leg(r, _localization) : string.Empty;

    /// <summary>
    /// Replaces the wrapped leg (same leg number) and re-raises the display properties that changed, without
    /// triggering the course save. A leg whose data is unchanged raises nothing.
    /// </summary>
    public void Apply(RelayTeamRow team, RelayLegRow leg, RelayCourseMode courseMode)
    {
        if (ReferenceEquals(_team, team) && _leg == leg && _courseMode == courseMode)
            return;

        IsApplying = true;
        try
        {
            var before = Snapshot();
            _team = team;
            _leg = leg;
            _courseMode = courseMode;
            BibCourseText = leg.BibCourse;
            var after = Snapshot();
            foreach (var (name, value) in after)
                if (!Equals(before[name], value))
                    OnPropertyChanged(name);
        }
        finally
        {
            IsApplying = false;
        }
    }

    // Every bound display property with its current value, so Apply can raise exactly the ones that changed.
    private Dictionary<string, object?> Snapshot() => new()
    {
        [nameof(TeamId)] = TeamId,
        [nameof(TeamNumber)] = TeamNumber,
        [nameof(IsBibCourseEditable)] = IsBibCourseEditable,
        [nameof(CourseText)] = CourseText,
        [nameof(HasCourseText)] = HasCourseText,
        [nameof(ParticipantId)] = ParticipantId,
        [nameof(Header)] = Header,
        [nameof(Runner)] = Runner,
        [nameof(HasRunner)] = HasRunner,
        [nameof(Chip)] = Chip,
        [nameof(ResultText)] = ResultText,
    };
}

/// <summary>Shared display text of relay results (the «Команди» page, the day grid, the read-out log).</summary>
public static class RelayText
{
    public static string Time(TimeSpan t) => t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    public static string TeamStatus(TeamDayResult r, ILocalizationService loc)
    {
        var parts = new List<string>();
        if (r.Status == FinishStatus.None)
            parts.Add(loc.Get("Relay.Pending"));
        else
            parts.Add(FinishStatusOptions.ShortCode(r.Status));
        if (r.Incomplete)
            parts.Add(loc.Get("Relay.Incomplete"));
        if (r.Restarted)
            parts.Add(loc.Get("Relay.WithRestart"));
        if (r.CourseSetOpen)
            parts.Add(loc.Get("Relay.CourseSetOpen"));
        return string.Join(" · ", parts);
    }

    public static string Leg(RelayLegResult r, ILocalizationService loc)
    {
        if (r.Pending)
            return loc.Get("Relay.Pending");
        var parts = new List<string>();
        if (r.Status != FinishStatus.None)
            parts.Add(FinishStatusOptions.ShortCode(r.Status));
        if (r.AdjustedTime is { } t)
            parts.Add(Time(t));
        if (r.Cumulative is { } c)
            parts.Add("Σ " + Time(c));
        if (r.Restarted)
            parts.Add(loc.Get("Relay.Restart"));
        return string.Join(" · ", parts);
    }
}
