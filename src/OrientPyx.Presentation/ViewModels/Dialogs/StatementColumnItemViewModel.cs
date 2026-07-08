using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// One configurable column in the participant-statement settings list: the column it represents, its localized
/// caption, and whether it is shown. The list order is the on-page column order (reordered by dragging the
/// preview headers). Mirrors <c>ProtocolColumnItemViewModel</c> for the статемент column enum.
/// </summary>
public sealed partial class StatementColumnItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly string _captionKey;

    public StatementColumnItemViewModel(StatementColumn column, string captionKey, bool visible, ILocalizationService localization)
    {
        Column = column;
        _captionKey = captionKey;
        _visible = visible;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Caption));
    }

    public StatementColumn Column { get; }

    /// <summary>Localized column caption, re-raised on language change.</summary>
    public string Caption => _localization.Get(_captionKey);

    [ObservableProperty]
    private bool _visible;

    /// <summary>Snapshots this item back into the persisted setting.</summary>
    public StatementColumnSetting ToSetting() => new() { Column = Column, Visible = Visible };
}
