using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One per-discount checkbox on a participant row: whether the participant gets this discount. The
/// participant rows carry a parallel <c>DiscountFlags</c> list (one entry per discount column, in the
/// same order), and the discount column's checkbox binds <c>DiscountFlags[i].IsSelected</c>.
///
/// Toggling persists in the background and recomputes the row's total fee, both through the
/// <c>onChanged</c> callback the owning row supplies. The FSOU-member discount uses a disabled
/// checkbox bound to the same flag — its value is driven by the participant's «Член ФСОУ» field, not
/// by clicking — so the box still reflects state without being editable.
/// </summary>
public sealed partial class DiscountFlagViewModel : ObservableObject
{
    private readonly Action<DiscountFlagViewModel>? _onChanged;
    private bool _initialized;

    [ObservableProperty]
    private bool _isSelected;

    public DiscountFlagViewModel(Guid discountId, bool isFsouMember, bool isSelected, Action<DiscountFlagViewModel>? onChanged)
    {
        DiscountId = discountId;
        IsFsouMemberDiscount = isFsouMember;
        _isSelected = isSelected;
        _onChanged = onChanged;
        _initialized = true;
    }

    /// <summary>The discount this flag toggles.</summary>
    public Guid DiscountId { get; }

    /// <summary>True for the auto-applied FSOU-member discount (read-only, driven by «Член ФСОУ»).</summary>
    public bool IsFsouMemberDiscount { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_initialized)
            _onChanged?.Invoke(this);
    }

    /// <summary>Sets the flag without firing the change callback (used to mirror «Член ФСОУ» onto the
    /// FSOU-member flag, or to seed state).</summary>
    public void SetSilently(bool value)
    {
        var was = _initialized;
        _initialized = false;
        IsSelected = value;
        _initialized = was;
    }
}
