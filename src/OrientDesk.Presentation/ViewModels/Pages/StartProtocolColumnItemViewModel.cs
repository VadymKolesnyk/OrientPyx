using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One configurable column in the start-protocol settings list: the column it represents, its localized
/// caption, and whether it is shown. The list order is the on-page column order; the page reorders this
/// collection with up/down or by dragging the preview header. Mirrors
/// <see cref="ProtocolColumnItemViewModel"/> for the start-protocol column enum.
/// </summary>
public sealed partial class StartProtocolColumnItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string _captionKey;

    public StartProtocolColumnItemViewModel(StartProtocolColumn column, string captionKey, bool visible, ILocalizationService localization)
    {
        Column = column;
        _captionKey = captionKey;
        _visible = visible;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Caption));
    }

    public StartProtocolColumn Column { get; }

    /// <summary>Localized column caption, re-raised on language change.</summary>
    public string Caption => _localization.Get(_captionKey);

    [ObservableProperty]
    private bool _visible;

    /// <summary>Snapshots this item back into the persisted setting.</summary>
    public StartProtocolColumnSetting ToSetting() => new() { Column = Column, Visible = Visible };
}
