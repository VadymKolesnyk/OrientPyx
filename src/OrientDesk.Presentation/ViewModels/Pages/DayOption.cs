using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

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

    /// <summary>The wrapped day, or null for the roster sentinel.</summary>
    public EventDay? Day { get; }

    /// <summary>True for the roster ("Мандатка") aggregate option; false for a real day.</summary>
    public bool IsRoster { get; }

    /// <summary>The day number, or 0 for the roster sentinel.</summary>
    public int Number => Day?.Number ?? 0;

    /// <summary>"Day 1"-style label (or the roster label), re-raised on language change.</summary>
    public string Label => IsRoster
        ? _localization.Get(_rosterLabelKey!)
        : $"{_localization.Get("Header.Day")} {Number}";

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
