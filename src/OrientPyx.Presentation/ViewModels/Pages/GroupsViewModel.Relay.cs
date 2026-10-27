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
        }
        finally
        {
            _suppressRelaySave = false;
        }
        IsRelayEditorVisible = true;
    }

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
            RestartRanking = SelectedRelayRestartRanking?.Value ?? RelayRestartRanking.AfterClean
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

/// <summary>A localized <see cref="RelayRestartRanking"/> choice.</summary>
public sealed class RelayRestartRankingOption(RelayRestartRanking value, ILocalizationService localization)
{
    public RelayRestartRanking Value { get; } = value;

    public string Label { get; } = localization.Get("Groups.Relay.RestartRanking." + value);

    public override string ToString() => Label;
}
