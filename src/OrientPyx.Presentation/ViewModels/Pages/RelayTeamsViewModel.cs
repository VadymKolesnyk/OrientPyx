using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
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
/// leg; a runner who does not run the day yet is added to it in the team's group. «Масове створення» adds N teams
/// numbered from X.
/// </summary>
public sealed partial class RelayTeamsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();
    private IReadOnlyList<RelayRunner> _runners = [];
    private bool _syncingDay;

    public RelayTeamsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore,
        IDayLockService dayLock)
        : base(localization)
    {
        UseDayLock(dayLock);
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
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

    /// <summary>The day's relay groups (team group combo, bulk-create target).</summary>
    public ObservableCollection<RelayGroupOption> GroupOptions { get; } = [];

    public bool HasRelayGroups => GroupOptions.Count > 0;

    public IReadOnlyList<ClubOption> ClubOptions { get; private set; } = [];

    public IReadOnlyList<RegionOption> RegionOptions { get; private set; } = [];

    // ── «Масове створення команд»
    [ObservableProperty]
    private RelayGroupOption? _bulkGroup;

    [ObservableProperty]
    private string _bulkCountText = "10";

    [ObservableProperty]
    private string _bulkFirstNumberText = "100";

    // ── Runner search for the legs panel
    [ObservableProperty]
    private string _runnerQuery = string.Empty;

    public ObservableCollection<RelayRunner> RunnerMatches { get; } = [];

    [ObservableProperty]
    private RelayRunner? _selectedRunner;

    public async Task LoadAsync()
    {
        CancelAllTimers();
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
            DayOptions.Clear();
            foreach (var day in days)
                DayOptions.Add(new DayOption(day, Localization));
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == _session.CurrentDay?.Number) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));
        RefreshDayLock();

        ClubOptions = [ClubOption.None(Localization), .. clubs.Select(c => new ClubOption(c.Id, c.Name, Localization))];
        RegionOptions = [RegionOption.None(Localization), .. regions.Select(r => new RegionOption(r.Id, r.Name, Localization))];
        OnPropertyChanged(nameof(ClubOptions));
        OnPropertyChanged(nameof(RegionOptions));

        GroupOptions.Clear();
        foreach (var g in data.Groups)
            GroupOptions.Add(new RelayGroupOption(g.GroupId, g.GroupName, g.LegCount));
        OnPropertyChanged(nameof(HasRelayGroups));
        BulkGroup = GroupOptions.FirstOrDefault(o => o.GroupId == BulkGroup?.GroupId) ?? GroupOptions.FirstOrDefault();

        var selectedId = SelectedTeam?.Id;
        Teams.Clear();
        foreach (var team in data.Teams)
            Teams.Add(new RelayTeamRowViewModel(team, Localization, GroupOptions, ClubOptions, RegionOptions, QueueSave));
        _runners = data.Runners;
        SelectedTeam = Teams.FirstOrDefault(t => t.Id == selectedId) ?? Teams.FirstOrDefault();
        RefreshRunnerMatches();
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null || _session.CurrentDay?.Number == value.Number)
            return;
        _ = _busy.RunAsync(() => _session.SetCurrentDayAsync(value.Day));
    }

    partial void OnSelectedTeamChanged(RelayTeamRowViewModel? value) => RebuildLegs();

    private void RebuildLegs()
    {
        Legs.Clear();
        if (SelectedTeam is not { } team)
            return;
        foreach (var leg in team.Model.Legs)
            Legs.Add(new RelayLegRowViewModel(team.Model, leg, Localization));
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

    [RelayCommand]
    private async Task CreateTeamsAsync()
    {
        if (BulkGroup is not { } group || !await EnsureDayEditableAsync())
            return;
        if (!int.TryParse(BulkCountText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0
            || !int.TryParse(BulkFirstNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first <= 0)
            return;
        await _busy.RunAsync(() => _editor.CreateRelayTeamsAsync(group.GroupId, count, first));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddTeamAsync()
    {
        if (BulkGroup is not { } group || !await EnsureDayEditableAsync())
            return;
        var next = Teams.Count == 0 ? 1 : Teams.Max(t => t.Model.Number) + 1;
        await _busy.RunAsync(() => _editor.CreateRelayTeamsAsync(group.GroupId, 1, next));
        await LoadAsync();
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
        if (IsDayLocked)
            return;
        CancelTimer(row.Id);
        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveAsync();

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
public sealed class RelayGroupOption(Guid groupId, string name, int legCount)
{
    public Guid GroupId { get; } = groupId;

    public string Label { get; } = name;

    public int LegCount { get; } = legCount;

    public override string ToString() => Label;
}

/// <summary>One editable team row of the «Команди» table.</summary>
public sealed partial class RelayTeamRowViewModel : ObservableObject
{
    private readonly Action<RelayTeamRowViewModel> _requestSave;
    private readonly bool _initialized;

    public RelayTeamRowViewModel(
        RelayTeamRow model, ILocalizationService localization,
        IReadOnlyList<RelayGroupOption> groups, IReadOnlyList<ClubOption> clubs, IReadOnlyList<RegionOption> regions,
        Action<RelayTeamRowViewModel> requestSave)
    {
        Model = model;
        Localization = localization;
        _requestSave = requestSave;
        GroupOptions = groups;
        ClubOptions = clubs;
        RegionOptions = regions;
        _numberText = model.Number.ToString(CultureInfo.InvariantCulture);
        _name = model.Name;
        _selectedGroup = groups.FirstOrDefault(g => g.GroupId == model.GroupId) ?? groups.FirstOrDefault();
        _selectedClub = clubs.FirstOrDefault(c => c.Id == model.ClubId) ?? clubs[0];
        _selectedRegion = regions.FirstOrDefault(r => r.Id == model.RegionId) ?? regions[0];
        _outOfCompetition = model.OutOfCompetition;
        StatusOptions = FinishStatusOptions.Build(localization, model.Result?.Computed ?? FinishStatus.None);
        _selectedStatus = FinishStatusOptions.Select(StatusOptions, model.StatusOverride);
        _initialized = true;
    }

    public RelayTeamRow Model { get; }

    public ILocalizationService Localization { get; }

    public Guid Id => Model.Id;

    public IReadOnlyList<RelayGroupOption> GroupOptions { get; }

    public IReadOnlyList<ClubOption> ClubOptions { get; }

    public IReadOnlyList<RegionOption> RegionOptions { get; }

    public IReadOnlyList<FinishStatusOption> StatusOptions { get; }

    [ObservableProperty]
    private string _numberText;

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

    /// <summary>The typed number is taken by another team of the day (Р-20).</summary>
    [ObservableProperty]
    private bool _numberError;

    /// <summary>«2/3»: assigned legs of the leg count.</summary>
    public string LegsText => $"{Model.Legs.Count(l => l.LinkId is not null)}/{Model.Legs.Count}";

    public string StatusText => Model.Result is { } r ? RelayText.TeamStatus(r, Localization) : string.Empty;

    public string TimeText => Model.Result?.Time is { } t ? RelayText.Time(t) : string.Empty;

    public string PlaceText => Model.Result is { } r
        ? r.Place?.ToString(CultureInfo.InvariantCulture)
          ?? (r.OutOfCompetition ? ParticipantDayResult.OutOfCompetitionMark : string.Empty)
        : string.Empty;

    public int PlaceSort => Model.Result?.Place ?? int.MaxValue;

    public RelayTeamEdit? ToEdit()
    {
        if (!int.TryParse(NumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
            || SelectedGroup is null)
            return null;
        return new RelayTeamEdit(Id, SelectedGroup.GroupId, number, Name ?? string.Empty,
            SelectedClub.Id, SelectedRegion.Id, OutOfCompetition, SelectedStatus.Status);
    }

    partial void OnNumberTextChanged(string value) => QueueSave();
    partial void OnNameChanged(string value) => QueueSave();
    partial void OnSelectedGroupChanged(RelayGroupOption? value) => QueueSave();
    partial void OnSelectedClubChanged(ClubOption value) => QueueSave();
    partial void OnSelectedRegionChanged(RegionOption value) => QueueSave();
    partial void OnOutOfCompetitionChanged(bool value) => QueueSave();
    partial void OnSelectedStatusChanged(FinishStatusOption value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }
}

/// <summary>One leg of the selected team in the legs panel.</summary>
public sealed class RelayLegRowViewModel(RelayTeamRow team, RelayLegRow leg, ILocalizationService localization)
{
    public Guid TeamId { get; } = team.Id;

    public int Leg { get; } = leg.Leg;

    public Guid? ParticipantId { get; } = leg.ParticipantId;

    /// <summary>«Етап 2 · 100.2».</summary>
    public string Header { get; } = string.Format(localization.Get("RelayTeams.Leg.Header"), leg.Leg,
        RelayBib.Format(team.Number, leg.Leg));

    public string Runner { get; } = leg.LinkId is null ? localization.Get("RelayTeams.Leg.Empty") : leg.FullName;

    public bool HasRunner => ParticipantId is not null;

    public string Chip { get; } = leg.Chip;

    /// <summary>The leg's computed state: status · time · cumulative, «очікує», «перезапуск».</summary>
    public string ResultText { get; } = leg.Result is { } r ? RelayText.Leg(r, localization) : string.Empty;
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
