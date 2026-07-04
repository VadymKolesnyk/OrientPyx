using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One day in the summary-protocol day list: its identity, a "counted" toggle (include in the total + show its
/// column band), and a localized label. The list order is the on-page left-to-right band order (reordered with
/// up/down). Toggling <see cref="Counted"/> or moving the day refreshes the preview and auto-saves.
/// </summary>
public sealed partial class SummaryDayItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public SummaryDayItemViewModel(Guid dayId, int dayNumber, bool counted, ILocalizationService localization)
    {
        DayId = dayId;
        DayNumber = dayNumber;
        _counted = counted;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    public Guid DayId { get; }
    public int DayNumber { get; }

    [ObservableProperty]
    private bool _counted;

    public string Label => $"{_localization.Get("Header.Day")} {DayNumber}";
}
