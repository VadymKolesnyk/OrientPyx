using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One configurable column in the protocol settings list: the column it represents, its localized caption,
/// and whether it is shown. The list order is the on-page column order; the page reorders this collection
/// with up/down. Backs a row in the column-configuration list on the Protocols page.
/// </summary>
public sealed partial class ProtocolColumnItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string _captionKey;

    public ProtocolColumnItemViewModel(ProtocolColumn column, string captionKey, bool visible, ILocalizationService localization)
    {
        Column = column;
        _captionKey = captionKey;
        _visible = visible;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Caption));
    }

    public ProtocolColumn Column { get; }

    /// <summary>Localized column caption, re-raised on language change.</summary>
    public string Caption => _localization.Get(_captionKey);

    [ObservableProperty]
    private bool _visible;

    /// <summary>Snapshots this item back into the persisted setting.</summary>
    public ProtocolColumnSetting ToSetting() => new() { Column = Column, Visible = Visible };
}
