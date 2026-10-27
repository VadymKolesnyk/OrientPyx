using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

// The relay group's settings strip (Р-27…Р-29, Р-35) under the groups table: legs, mass start of leg 1, a restart
// time per leg 2…N and how a restarted team is ranked. Edits debounce-save the whole set.
public sealed partial class GroupsViewModel
{
    private static readonly TimeSpan RelaySaveDebounce = TimeSpan.FromMilliseconds(600);

    private CancellationTokenSource? _relaySaveCts;
    private bool _suppressRelaySave;
    private RelayGroupSettings? _relaySettings;

    /// <summary>True when the selected group runs a relay, so the relay settings show above its course.</summary>
    [ObservableProperty]
    private bool _isRelayEditorVisible;

    [ObservableProperty]
    private string _relayLegCountText = string.Empty;

    [ObservableProperty]
    private string _relayMassStartText = string.Empty;

    [ObservableProperty]
    private RelayRestartRankingOption? _selectedRelayRestartRanking;

    /// <summary>One restart-time box per leg 2…N.</summary>
    public ObservableCollection<RelayRestartItemViewModel> RelayRestarts { get; } = [];

    // ── Leg courses (Р-40…Р-44): how each leg gets its course; the named courses are the variants table below.

    [ObservableProperty]
    private RelayCourseModeOption? _selectedRelayCourseMode;

    /// <summary>«Набір на команду»: the group's default course set («A B»); a team's own set overrides it.</summary>
    [ObservableProperty]
    private string _relayTeamCourseSetText = string.Empty;

    /// <summary>«За етапом»: one course-code box per leg.</summary>
    public ObservableCollection<RelayLegCourseItemViewModel> RelayLegCourses { get; } = [];

    // True while the selected relay group names its leg courses — the variants table then edits them.
    private bool _relayNamedCourses;

    public bool IsRelayByLeg => SelectedRelayCourseMode?.Value == RelayCourseMode.ByLeg;

    public bool IsRelayByBib => SelectedRelayCourseMode?.Value == RelayCourseMode.ByBib;

    public bool IsRelayTeamSet => SelectedRelayCourseMode?.Value == RelayCourseMode.TeamSet;

    private IReadOnlyList<RelayCourseModeOption>? _relayCourseModeOptions;

    // Built once: the combo matches its selection by reference.
    public IReadOnlyList<RelayCourseModeOption> RelayCourseModeOptions => _relayCourseModeOptions ??=
        Enum.GetValues<RelayCourseMode>().Select(m => new RelayCourseModeOption(m, Localization)).ToList();

    private IReadOnlyList<RelayRestartRankingOption>? _relayRankingOptions;

    // Built once: the combo matches its selection by reference.
    public IReadOnlyList<RelayRestartRankingOption> RelayRestartRankingOptions => _relayRankingOptions ??=
        Enum.GetValues<RelayRestartRanking>().Select(r => new RelayRestartRankingOption(r, Localization)).ToList();

    private async Task RefreshRelayEditorAsync()
    {
        var row = SelectedGroup;
        if (row is null || !row.IsRelay)
        {
            _relaySettings = null;
            _relayNamedCourses = false;
            IsRelayEditorVisible = false;
            return;
        }

        var settings = await _editor.GetRelayGroupSettingsAsync(row.GroupId);
        if (settings is null || !ReferenceEquals(SelectedGroup, row))
            return;

        _suppressRelaySave = true;
        try
        {
            _relaySettings = settings;
            RelayLegCountText = settings.LegCount.ToString(CultureInfo.InvariantCulture);
            RelayMassStartText = StartTimeFormat.Format(settings.MassStartTime);
            SelectedRelayRestartRanking = RelayRestartRankingOptions.First(o => o.Value == settings.RestartRanking);
            RebuildRestartItems(settings.LegCount, settings.RestartTimes);
            SelectedRelayCourseMode = RelayCourseModeOptions.First(o => o.Value == settings.CourseMode);
            RelayTeamCourseSetText = settings.TeamCourseSet;
            RebuildLegCourseItems(settings.LegCount, settings.LegCourses ?? new Dictionary<int, string>());
        }
        finally
        {
            _suppressRelaySave = false;
        }
        IsRelayEditorVisible = true;
        SyncRelayCourseEditor();
    }

    // The variants table edits the relay's named courses whenever the mode uses them.
    private void SyncRelayCourseEditor()
    {
        var named = SelectedRelayCourseMode is { Value: not RelayCourseMode.Group };
        OnPropertyChanged(nameof(IsRelayByLeg));
        OnPropertyChanged(nameof(IsRelayByBib));
        OnPropertyChanged(nameof(IsRelayTeamSet));
        if (named == _relayNamedCourses && IsScatterEditorVisible == (named || SelectedGroup?.IsScatter == true))
            return;
        _relayNamedCourses = named;
        _ = RefreshScatterEditorAsync();
    }

    private void RebuildLegCourseItems(int legCount, IReadOnlyDictionary<int, string> courses)
    {
        foreach (var item in RelayLegCourses)
            item.Changed -= QueueRelaySave;
        RelayLegCourses.Clear();
        for (var leg = 1; leg <= Math.Min(legCount, 50); leg++)
        {
            var item = new RelayLegCourseItemViewModel(leg, Localization, courses.TryGetValue(leg, out var c) ? c : string.Empty);
            item.Changed += QueueRelaySave;
            RelayLegCourses.Add(item);
        }
    }

    partial void OnSelectedRelayCourseModeChanged(RelayCourseModeOption? value)
    {
        if (_suppressRelaySave)
            return;
        SyncRelayCourseEditor();
        QueueRelaySave();
    }

    partial void OnRelayTeamCourseSetTextChanged(string value) => QueueRelaySave();

    private void RebuildRestartItems(int legCount, IReadOnlyDictionary<int, TimeSpan> times)
    {
        foreach (var item in RelayRestarts)
            item.Changed -= QueueRelaySave;
        RelayRestarts.Clear();
        for (var leg = 2; leg <= Math.Min(legCount, 50); leg++)
        {
            var item = new RelayRestartItemViewModel(leg, Localization,
                times.TryGetValue(leg, out var t) ? StartTimeFormat.Format(t) : string.Empty);
            item.Changed += QueueRelaySave;
            RelayRestarts.Add(item);
        }
    }

    partial void OnRelayLegCountTextChanged(string value)
    {
        if (_suppressRelaySave)
            return;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var legs) && legs is >= 1 and <= 50)
        {
            var current = RelayRestarts.Where(r => r.Time is not null).ToDictionary(r => r.Leg, r => r.Time!.Value);
            RebuildRestartItems(legs, current);
            var courses = RelayLegCourses.Where(c => c.Code.Length > 0).ToDictionary(c => c.Leg, c => c.Code);
            RebuildLegCourseItems(legs, courses);
        }
        QueueRelaySave();
    }

    partial void OnRelayMassStartTextChanged(string value) => QueueRelaySave();

    partial void OnSelectedRelayRestartRankingChanged(RelayRestartRankingOption? value) => QueueRelaySave();

    private void QueueRelaySave()
    {
        if (_suppressRelaySave || _relaySettings is not { } current)
            return;
        if (!int.TryParse(RelayLegCountText, NumberStyles.None, CultureInfo.InvariantCulture, out var legs) || legs < 1)
            return;
        var massStart = StartTimeFormat.TryParse(RelayMassStartText, out var ms) ? ms : null;
        var restarts = RelayRestarts.Where(r => r.Time is not null).ToDictionary(r => r.Leg, r => r.Time!.Value);
        var updated = current with
        {
            LegCount = legs,
            MassStartTime = massStart,
            RestartTimes = restarts,
            RestartRanking = SelectedRelayRestartRanking?.Value ?? RelayRestartRanking.AfterClean,
            CourseMode = SelectedRelayCourseMode?.Value ?? RelayCourseMode.Group,
            LegCourses = RelayLegCourses.Where(c => c.Code.Length > 0).ToDictionary(c => c.Leg, c => c.Code),
            TeamCourseSet = RelayTeamCourseSetText ?? string.Empty
        };
        _relaySettings = updated;

        _relaySaveCts?.Cancel();
        var cts = _relaySaveCts = new CancellationTokenSource();
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await Task.Delay(RelaySaveDebounce, cts.Token);
                await Task.Run(() => _editor.SaveRelayGroupSettingsAsync(updated, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (DayLockedException)
            {
            }
        }
    }
}

/// <summary>A restart time box for one relay leg («Перезапуск етапу N»).</summary>
public sealed partial class RelayRestartItemViewModel : ObservableObject
{
    public RelayRestartItemViewModel(int leg, ILocalizationService localization, string text)
    {
        Leg = leg;
        Label = string.Format(localization.Get("Groups.Relay.RestartLeg"), leg);
        _text = text;
    }

    public int Leg { get; }

    public string Label { get; }

    [ObservableProperty]
    private string _text;

    /// <summary>The parsed time of day; null when blank or not a time.</summary>
    public TimeSpan? Time => StartTimeFormat.TryParse(Text, out var t) ? t : null;

    public event Action? Changed;

    partial void OnTextChanged(string value) => Changed?.Invoke();
}

/// <summary>A course-code box for one relay leg («Етап N: дистанція»), <see cref="RelayCourseMode.ByLeg"/>.</summary>
public sealed partial class RelayLegCourseItemViewModel : ObservableObject
{
    public RelayLegCourseItemViewModel(int leg, ILocalizationService localization, string code)
    {
        Leg = leg;
        Label = string.Format(localization.Get("Groups.Relay.LegCourse"), leg);
        _text = code;
    }

    public int Leg { get; }

    public string Label { get; }

    [ObservableProperty]
    private string _text;

    /// <summary>The trimmed course code; empty when blank.</summary>
    public string Code => (Text ?? string.Empty).Trim();

    public event Action? Changed;

    partial void OnTextChanged(string value) => Changed?.Invoke();
}

/// <summary>A localized <see cref="RelayCourseMode"/> choice.</summary>
public sealed class RelayCourseModeOption(RelayCourseMode value, ILocalizationService localization)
{
    public RelayCourseMode Value { get; } = value;

    public string Label { get; } = localization.Get("Groups.Relay.CourseMode." + value);

    public override string ToString() => Label;
}

/// <summary>A localized <see cref="RelayRestartRanking"/> choice.</summary>
public sealed class RelayRestartRankingOption(RelayRestartRanking value, ILocalizationService localization)
{
    public RelayRestartRanking Value { get; } = value;

    public string Label { get; } = localization.Get("Groups.Relay.RestartRanking." + value);

    public override string ToString() => Label;
}
