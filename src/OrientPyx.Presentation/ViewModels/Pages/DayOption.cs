using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable competition day with its localized "Day N" label, used as a ComboBox item in
/// the per-page day selector. Wraps a single <see cref="EventDay"/>.
/// </summary>
public sealed partial class DayOption : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string? _rosterLabelKey;

    public DayOption(EventDay day, ILocalizationService localization)
    {
        Day = day;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    private DayOption(ILocalizationService localization, string rosterLabelKey)
    {
        Day = null;
        IsRoster = true;
        _rosterLabelKey = rosterLabelKey;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>
    /// Creates the special roster ("Мандатка") option used by the participants page. It is not a
    /// real day — selecting it aggregates all days and must not change the session's current day.
    /// </summary>
    public static DayOption Roster(ILocalizationService localization, string labelKey)
        => new(localization, labelKey);

    /// <summary>
    /// The wrapped day, or null for the roster sentinel.
    ///
    /// Settable because the options list is deliberately reused across reloads (the ComboBox's
    /// SelectedItem must stay a valid reference), so the wrapped entity would otherwise stay the
    /// snapshot read when the list was first built. Fields that change under the page — above all
    /// <see cref="EventDay.IsLocked"/> — must be refreshed here, or selecting the option would push
    /// a stale day back into the session. See <see cref="Sync"/>.
    /// </summary>
    public EventDay? Day { get; private set; }

    /// <summary>
    /// Replaces the wrapped entity with the freshly-read one for the same day. Called by every page
    /// that keeps its <c>DayOptions</c> across a reload, so the option always carries the day's
    /// current state rather than the one it was built with.
    /// </summary>
    public void Sync(EventDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (IsRoster)
            return;

        Day = day;
        OnPropertyChanged(nameof(Label));
    }

    /// <summary>True for the roster ("Мандатка") aggregate option; false for a real day.</summary>
    public bool IsRoster { get; }

    /// <summary>The day number, or 0 for the roster sentinel.</summary>
    public int Number => Day?.Number ?? 0;

    /// <summary>"Day 1"-style label (or the roster label), re-raised on language change.</summary>
    public string Label => IsRoster
        ? _localization.Get(_rosterLabelKey!)
        : $"{_localization.Get("Header.Day")} {Number}";

    /// <summary>
    /// Brings a page's reused option list up to date with a fresh read of the days: each option that
    /// still stands for one of <paramref name="days"/> re-wraps the freshly-read entity.
    ///
    /// Pages keep their <c>DayOptions</c> across a reload so the ComboBox's SelectedItem stays a valid
    /// reference; without this the options would keep serving the entities they were built with, and
    /// picking a day would push a stale <see cref="EventDay.IsLocked"/> into the session.
    /// </summary>
    public static void SyncAll(IEnumerable<DayOption> options, IReadOnlyList<EventDay> days)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(days);

        foreach (var option in options)
        {
            if (option.IsRoster)
                continue;
            if (days.FirstOrDefault(d => d.Id == option.Day?.Id) is { } fresh)
                option.Sync(fresh);
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
