namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// The "by status" filter tokens of a chip column — whether the chip is in the competition's rental-chip
/// database (<see cref="RentalChipRegistry"/>, the same test that bold-reds a non-rental chip). The table reads
/// the state itself (it holds the registry), so a rental toggle is seen on the next filter pass.
/// </summary>
public static class RentalChipStatus
{
    public const string Rental = "RentalChip";
    public const string NonRental = "NonRentalChip";
    public const string NoChip = "NoChip";

    /// <summary>Tokens with their labels, in display order, for <c>SheetColumn.StatusOptions</c>.</summary>
    public static IReadOnlyList<(string Token, string LabelKey)> FilterOptions { get; } =
    [
        (Rental, "Sheet.Filter.RentalChip.Rental"),
        (NonRental, "Sheet.Filter.RentalChip.NonRental"),
        (NoChip, "Sheet.Filter.RentalChip.NoChip")
    ];

    /// <summary>The token of one chip value: blank ⇒ <see cref="NoChip"/>, else rental or not by the registry.</summary>
    public static string Of(RentalChipRegistry? registry, string? chip)
    {
        if (string.IsNullOrWhiteSpace(chip))
            return NoChip;
        return registry?.Contains(chip) == true ? Rental : NonRental;
    }
}
