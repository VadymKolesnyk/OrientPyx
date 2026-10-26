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
    // An additional field's own name (not a localization key); null for a built-in column.
    private readonly string? _fixedCaption;

    public StatementColumnItemViewModel(
        StatementColumn column, string captionKey, bool visible, ILocalizationService localization,
        string? fieldKey = null, string? fixedCaption = null)
    {
        Column = column;
        FieldKey = fieldKey;
        _captionKey = captionKey;
        _fixedCaption = fixedCaption;
        _visible = visible;
        _localization = localization;
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Caption));
    }

    public StatementColumn Column { get; }

    /// <summary>The additional field's key for a <see cref="StatementColumn.Field"/> column; null otherwise.</summary>
    public string? FieldKey { get; }

    /// <summary>The column's identity in the list (same as <see cref="StatementColumnSetting.Key"/>).</summary>
    public string Key => ToSetting().Key;

    /// <summary>Localized column caption, re-raised on language change.</summary>
    public string Caption => _fixedCaption ?? _localization.Get(_captionKey);

    [ObservableProperty]
    private bool _visible;

    /// <summary>Snapshots this item back into the persisted setting.</summary>
    public StatementColumnSetting ToSetting() => new() { Column = Column, FieldKey = FieldKey, Visible = Visible };
}
