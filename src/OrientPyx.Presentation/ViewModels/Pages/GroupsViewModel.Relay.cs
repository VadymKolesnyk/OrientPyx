using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

// The relay group's settings strip (Р-35, Р-41) under the groups table: legs, how a restarted team is ranked and the
// leg courses. The mass start of leg 1 and the restarts are not here — «Старт → Масовий старт» sets them — so a save
// re-reads the group's settings and changes only the strip's own fields. Edits debounce-save.
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
    private RelayRestartRankingOption? _selectedRelayRestartRanking;

    // ── Leg courses (Р-40…Р-44): one course for every leg, by leg number, or a team set; the named courses are the
    // variants table below.

    [ObservableProperty]
    private RelayCourseModeOption? _selectedRelayCourseMode;

    /// <summary>«Набір на команду»: the group's default course set («A B»); a team's own set overrides it.</summary>
    [ObservableProperty]
    private string _relayTeamCourseSetText = string.Empty;

    // True while the selected relay group names its leg courses — the variants table then edits them.
    private bool _relayNamedCourses;

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
            row.ShowLegCount(settings.LegCount);
            SelectedRelayRestartRanking = RelayRestartRankingOptions.First(o => o.Value == settings.RestartRanking);
            SelectedRelayCourseMode = RelayCourseModeOptions.First(o => o.Value == settings.CourseMode);
            RelayTeamCourseSetText = settings.TeamCourseSet;
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
        OnPropertyChanged(nameof(IsRelayByBib));
        OnPropertyChanged(nameof(IsRelayTeamSet));
        if (named == _relayNamedCourses && IsScatterEditorVisible == (named || SelectedGroup?.IsScatter == true))
            return;
        _relayNamedCourses = named;
        _ = RefreshScatterEditorAsync();
    }

    partial void OnSelectedRelayCourseModeChanged(RelayCourseModeOption? value)
    {
        if (_suppressRelaySave)
            return;
        SyncRelayCourseEditor();
        QueueRelaySave();
    }

    partial void OnRelayTeamCourseSetTextChanged(string value) => QueueRelaySave();

    partial void OnRelayLegCountTextChanged(string value)
    {
        if (_suppressRelaySave)
            return;
        // The grid's «Етапів» cell follows the box.
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var legs) && legs is >= 1 and <= 50
            && SelectedGroup is { } row && row.GroupId == _relaySettings?.GroupId)
            row.ShowLegCount(legs);
        QueueRelaySave();
    }

    private readonly Dictionary<Guid, CancellationTokenSource> _legCountTimers = new();

    // «Етапів» typed in the grid. The selected group's settings strip takes it (and saves it); another group's relay
    // settings are read, changed and saved after a pause.
    private void RequestLegCountSave(GroupDayRowViewModel row)
    {
        if (row.IsApplying || IsDayLocked || !row.IsRelay || row.LegCount is not { } legs)
            return;
        if (ReferenceEquals(row, SelectedGroup) && _relaySettings?.GroupId == row.GroupId)
        {
            RelayLegCountText = legs.ToString(CultureInfo.InvariantCulture);
            return;
        }

        if (_legCountTimers.Remove(row.GroupId, out var previous))
            previous.Cancel();
        var cts = new CancellationTokenSource();
        _legCountTimers[row.GroupId] = cts;
        _pendingSaves.Track(SaveAsync());

        async Task SaveAsync()
        {
            try
            {
                await Task.Delay(RelaySaveDebounce, cts.Token);
                var settings = await Task.Run(() => _editor.GetRelayGroupSettingsAsync(row.GroupId, cts.Token), cts.Token);
                if (settings is not null && settings.LegCount != legs)
                    await Task.Run(() => _editor.SaveRelayGroupSettingsAsync(settings with { LegCount = legs }, cts.Token), cts.Token);
                // The group got selected meanwhile: its settings strip reads the saved count.
                if (ReferenceEquals(row, SelectedGroup))
                    await RefreshRelayEditorAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (DayLockedException)
            {
            }
        }
    }

    private void CancelLegCountTimers()
    {
        foreach (var cts in _legCountTimers.Values)
            cts.Cancel();
        _legCountTimers.Clear();
    }

    partial void OnSelectedRelayRestartRankingChanged(RelayRestartRankingOption? value) => QueueRelaySave();

    private void QueueRelaySave()
    {
        if (_suppressRelaySave || _relaySettings is not { } current)
            return;
        if (!int.TryParse(RelayLegCountText, NumberStyles.None, CultureInfo.InvariantCulture, out var legs) || legs < 1)
            return;
        var ranking = SelectedRelayRestartRanking?.Value ?? RelayRestartRanking.AfterClean;
        var courseMode = SelectedRelayCourseMode?.Value ?? RelayCourseMode.Group;
        var teamCourseSet = RelayTeamCourseSetText ?? string.Empty;
        _relaySettings = current with
        {
            LegCount = legs, RestartRanking = ranking, CourseMode = courseMode, TeamCourseSet = teamCourseSet
        };
        var groupId = current.GroupId;

        _relaySaveCts?.Cancel();
        var cts = _relaySaveCts = new CancellationTokenSource();
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await Task.Delay(RelaySaveDebounce, cts.Token);
                // Fresh: the mass start and restarts may have been set on «Масовий старт» since this group was shown.
                var fresh = await Task.Run(() => _editor.GetRelayGroupSettingsAsync(groupId, cts.Token), cts.Token);
                if (fresh is null)
                    return;
                var updated = fresh with
                {
                    LegCount = legs, RestartRanking = ranking, CourseMode = courseMode, TeamCourseSet = teamCourseSet
                };
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
