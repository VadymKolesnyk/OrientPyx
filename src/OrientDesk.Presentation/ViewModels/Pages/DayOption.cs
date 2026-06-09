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

    public DayOption(EventDay day, ILocalizationService localization)
    {
        Day = day;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public EventDay Day { get; }

    public int Number => Day.Number;

    /// <summary>"Day 1"-style label, re-raised on language change.</summary>
    public string Label => $"{_localization.Get("Header.Day")} {Day.Number}";

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
