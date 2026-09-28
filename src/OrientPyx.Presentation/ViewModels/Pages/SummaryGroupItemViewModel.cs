using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One group in the summary-protocol group list: an "included" toggle. <see cref="IsExplicit"/> is false while the
/// state follows the default rule (non-zero result ⇒ included); the first user toggle makes it an explicit choice.
/// </summary>
public sealed partial class SummaryGroupItemViewModel : ObservableObject
{
    public SummaryGroupItemViewModel(Guid groupId, string name, bool included, bool isExplicit)
    {
        GroupId = groupId;
        Name = name;
        _included = included;
        IsExplicit = isExplicit;
    }

    public Guid GroupId { get; }
    public string Name { get; }

    public bool IsExplicit { get; set; }

    [ObservableProperty]
    private bool _included;
}
