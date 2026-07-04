using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable draw separation field (None / Region / Club / Team) with its localized label, used as a
/// ComboBox item on the draw page. Chooses which attribute the draw keeps off consecutive start slots.
/// </summary>
public sealed partial class DrawSeparationOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public DrawSeparationOption(DrawSeparationField value, ILocalizationService localization)
    {
        Value = value;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public DrawSeparationField Value { get; }

    public string Label => _localization.Get("Draw.Separation." + Value);

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
