using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One row in the chip-price-override table: a chip note (примітка) plus the rental price per day for
/// chips carrying that note. Both fields edit in the background (debounced per row) via the
/// page-supplied <c>requestSave</c> callback, mirroring <see cref="RegionRowViewModel"/>. The price is
/// held as text so an empty cell parses to 0.
/// </summary>
public sealed partial class ChipPriceOverrideRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTimeOffset _createdAt;
    private readonly Action<ChipPriceOverrideRowViewModel> _requestSave;
    private readonly bool _initialized;

    /// <summary>The chip note this rule matches.</summary>
    [ObservableProperty]
    private string _note;

    /// <summary>Price per day as editable text. Parsed back to <see cref="decimal"/> on save.</summary>
    [ObservableProperty]
    private string _priceText;

    public ChipPriceOverrideRowViewModel(
        ChipPriceOverride priceOverride,
        ILocalizationService localization,
        Action<ChipPriceOverrideRowViewModel> requestSave)
    {
        _id = priceOverride.Id;
        _createdAt = priceOverride.CreatedAt;
        _requestSave = requestSave;
        Localization = localization;

        _note = priceOverride.Note;
        _priceText = FormatDecimal(priceOverride.PricePerDay);

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public ChipPriceOverride ToEntity() => new()
    {
        Id = _id,
        Note = (Note ?? string.Empty).Trim(),
        PricePerDay = ParseDecimal(PriceText),
        CreatedAt = _createdAt
    };

    partial void OnNoteChanged(string value) => QueueSave();
    partial void OnPriceTextChanged(string value) => QueueSave();

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
