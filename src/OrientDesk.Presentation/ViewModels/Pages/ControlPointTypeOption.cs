using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable control-point type with its localized label, used as a ComboBox item.
/// The label re-resolves when the interface language changes.
/// </summary>
public sealed partial class ControlPointTypeOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public ControlPointTypeOption(ControlPointType value, ILocalizationService localization)
    {
        Value = value;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public ControlPointType Value { get; }

    public string Label => _localization.Get("ControlPoints.Type." + Value);

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
