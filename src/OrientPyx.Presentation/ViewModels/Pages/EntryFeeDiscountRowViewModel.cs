using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One row in the discounts table: a discount's name, its percentage, and whether it also applies to
/// chip rental. All fields edit in the background (debounced per row) via the page-supplied
/// <c>requestSave</c> callback, mirroring <see cref="RegionRowViewModel"/>. The percent is held as text
/// so an empty cell parses to 0.
/// </summary>
public sealed partial class EntryFeeDiscountRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTimeOffset _createdAt;
    private readonly bool _isFsouMemberDiscount;
    private readonly Action<EntryFeeDiscountRowViewModel> _requestSave;
    private readonly bool _initialized;

    /// <summary>Discount display name.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Discount percentage as editable text. Parsed back to <see cref="decimal"/> on save.</summary>
    [ObservableProperty]
    private string _percentText;

    /// <summary>When true, the discount also reduces the chip-rental charge.</summary>
    [ObservableProperty]
    private bool _appliesToChipRental;

    public EntryFeeDiscountRowViewModel(
        EntryFeeDiscount discount,
        ILocalizationService localization,
        Action<EntryFeeDiscountRowViewModel> requestSave)
    {
        _id = discount.Id;
        _createdAt = discount.CreatedAt;
        _isFsouMemberDiscount = discount.IsFsouMemberDiscount;
        _requestSave = requestSave;
        Localization = localization;

        _name = discount.Name;
        _percentText = FormatDecimal(discount.Percent);
        _appliesToChipRental = discount.AppliesToChipRental;

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    /// <summary>True for the seeded, auto-applied FSOU-member discount.</summary>
    public bool IsFsouMemberDiscount => _isFsouMemberDiscount;

    /// <summary>The FSOU-member discount is permanent; every other discount can be deleted. Bound by
    /// the delete button's visibility.</summary>
    public bool CanDelete => !_isFsouMemberDiscount;

    public EntryFeeDiscount ToEntity() => new()
    {
        Id = _id,
        Name = (Name ?? string.Empty).Trim(),
        Percent = ParseDecimal(PercentText),
        AppliesToChipRental = AppliesToChipRental,
        IsFsouMemberDiscount = _isFsouMemberDiscount,
        CreatedAt = _createdAt
    };

    partial void OnNameChanged(string value) => QueueSave();
    partial void OnPercentTextChanged(string value) => QueueSave();
    partial void OnAppliesToChipRentalChanged(bool value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    private static string FormatDecimal(decimal value)
        => value == 0 ? string.Empty : value.ToString("0.######", CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var normalized = text.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
