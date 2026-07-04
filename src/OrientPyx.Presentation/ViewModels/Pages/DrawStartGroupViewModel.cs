using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One start group ("Стартова група") on the draw page: an ordered list of groups whose competitors start
/// one after another on the same start lane, all beginning at the global start time. Mirrors a column in
/// the CourseParser draw modal. The contained groups can be moved between start groups and reordered; the
/// page recomputes the per-group start times whenever the arrangement, start or interval changes.
/// </summary>
public sealed partial class DrawStartGroupViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public DrawStartGroupViewModel(int index, ILocalizationService localization)
    {
        Index = index;
        _localization = localization;
        _localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(FooterLabel));
        };
    }

    /// <summary>Zero-based position; drives the "Стартова група N" header.</summary>
    [ObservableProperty]
    private int _index;

    /// <summary>The groups in this start group, in start order.</summary>
    public ObservableCollection<DrawGroupItemViewModel> Groups { get; } = [];

    /// <summary>"Стартова група 1"-style header, re-raised on language change / index change.</summary>
    public string Title => $"{_localization.Get("Draw.StartGroup")} {Index + 1}";

    /// <summary>Total competitors across all groups in this start group.</summary>
    public int TotalMembers => Groups.Sum(g => g.MemberCount);

    /// <summary>"Останній старт: 11:23:00"-style footer; blank when the start group is empty.</summary>
    [ObservableProperty]
    private string _footerText = string.Empty;

    /// <summary>True while a drag is hovering this column — the View tints/outlines it as the drop target.</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>True while a drag would drop at the END of this column — the View shows an insertion line below the last chip.</summary>
    [ObservableProperty]
    private bool _showDropLineAtEnd;

    public string FooterLabel => string.IsNullOrEmpty(FooterText)
        ? string.Empty
        : $"{_localization.Get("Draw.LastStart")}: {FooterText}";

    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(Title));

    partial void OnFooterTextChanged(string value) => OnPropertyChanged(nameof(FooterLabel));

    public void RaiseTotalsChanged()
    {
        OnPropertyChanged(nameof(TotalMembers));
    }
}
