using System.Globalization;

namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>
/// SportIdent card model and punch capacity, read off the card number alone: SPORTident assigns every card
/// model its own number range, and how many control punches (with time) a card holds is fixed per model.
/// Pure lookup — no station, no read-out needed — so the roster can flag a chip too small for the day's
/// course before the runner starts.
/// </summary>
public static class SiCard
{
    /// <summary>
    /// One card model: its name as printed in SPORTident documentation («SI-Card 9», «SIAC»), the number of
    /// control punches with time it always holds, and — for a model whose capacity depends on the station
    /// configuration (SI-Card 6 writes 192 punches only when the stations are set up for it) — the larger
    /// capacity it may reach. Capacity checks use the guaranteed <see cref="Capacity"/>.
    /// </summary>
    public sealed record Info(string Model, int Capacity, int? ExtendedCapacity = null)
    {
        /// <summary>«50», or «64 (192)» for a model with a configuration-dependent extended capacity.</summary>
        public string CapacityText => ExtendedCapacity is { } extended
            ? Capacity.ToString(CultureInfo.InvariantCulture) + " (" + extended.ToString(CultureInfo.InvariantCulture) + ")"
            : Capacity.ToString(CultureInfo.InvariantCulture);
    }

    // Number ranges per model (SPORTident «SI-Card number ranges»). Order does not matter — the ranges are
    // disjoint. SI-Card 5 also holds 6 more control codes without time; those are not counted here because
    // a result without split times is still a result, but the warning should be about punches that are kept.
    private static readonly (long From, long To, Info Info)[] Ranges =
    [
        (1, 499_999, new Info("SI-Card 5", 30)),
        (500_000, 999_999, new Info("SI-Card 6", 64, 192)),
        (1_000_000, 1_999_999, new Info("SI-Card 9", 50)),
        (2_000_000, 2_999_999, new Info("SI-Card 8", 30)),
        (4_000_000, 4_999_999, new Info("pCard", 20)),
        (6_000_000, 6_999_999, new Info("tCard", 25)),
        (7_000_000, 7_999_999, new Info("SI-Card 10", 128)),
        (8_000_000, 8_999_999, new Info("SIAC", 128)),
        (9_000_000, 9_999_999, new Info("SI-Card 11", 128)),
        (14_000_000, 14_999_999, new Info("fCard", 20)),
        (16_711_680, 16_777_215, new Info("SI-Card 6*", 192)),
    ];

    /// <summary>
    /// The card model behind <paramref name="number"/>, or null for a blank, non-numeric or unassigned number
    /// (an unknown card is never judged — a null never raises a warning).
    /// </summary>
    public static Info? Identify(string? number)
    {
        var text = (number ?? string.Empty).Trim();
        if (text.Length == 0 || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;

        foreach (var (from, to, info) in Ranges)
            if (n >= from && n <= to)
                return info;
        return null;
    }

    /// <summary>
    /// The card when it is a known model that cannot hold <paramref name="punches"/> control punches; null when
    /// it fits, when the card is unknown, or when nothing is needed (<paramref name="punches"/> ≤ 0).
    /// </summary>
    public static Info? TooSmall(string? number, int punches) =>
        punches > 0 && Identify(number) is { } card && card.Capacity < punches ? card : null;
}
