namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Who holds each chip, split by competition day — the rental-chip grid shows one "assigned to" column
/// per day. <see cref="DayNumbers"/> lists the competition's days in order; each entry of
/// <see cref="HoldersByChip"/> is aligned with it (index i = day <c>DayNumbers[i]</c>) and holds the
/// holder's full name on that day, or an empty string when nobody holds the chip that day.
/// </summary>
public sealed record RentalChipHoldersByDay(
    IReadOnlyList<int> DayNumbers,
    IReadOnlyDictionary<string, IReadOnlyList<string>> HoldersByChip)
{
    public static RentalChipHoldersByDay Empty { get; } =
        new([], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
}
