using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable group rank level (Додаток 89) with its localized label, used as a ComboBox item for a
/// group's per-day rank level. The label re-resolves when the language changes.
/// </summary>
public sealed partial class RankLevelOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public RankLevelOption(GroupRankLevel value, ILocalizationService localization)
    {
        Value = value;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public GroupRankLevel Value { get; }

    public string Label => _localization.Get("Groups.RankLevel." + Value);

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
