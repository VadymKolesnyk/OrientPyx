using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One configurable leading column in the summary-protocol settings list: the column it represents, its
/// localized caption, and whether it is shown. The list order is the on-page order of the leading columns
/// (the per-day result bands + «Сума» always follow them); the page reorders this collection with up/down.
/// </summary>
public sealed partial class SummaryColumnItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string _captionKey;

    public SummaryColumnItemViewModel(SummaryColumn column, string captionKey, bool visible, ILocalizationService localization)
    {
        Column = column;
        _captionKey = captionKey;
        _visible = visible;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Caption));
    }

    public SummaryColumn Column { get; }

    /// <summary>Localized column caption, re-raised on language change.</summary>
    public string Caption => _localization.Get(_captionKey);

    [ObservableProperty]
    private bool _visible;

    /// <summary>Snapshots this item back into the persisted setting.</summary>
    public SummaryColumnSetting ToSetting() => new() { Column = Column, Visible = Visible };
}
