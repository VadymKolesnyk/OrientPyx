using System.Globalization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Reading and writing the free-text «Оплата» value as a number. A payment cell is plain text — a judge
/// may type a note there instead of a sum — so every numeric use (the paid/owed status tint, the status-bar
/// sums, redistributing a payment across days) has to agree on what counts as a number. That agreement
/// lives here: both the invariant ('.') and the current-culture decimal separators are accepted, and a
/// value written back is formatted invariantly with the trailing zeros trimmed.
/// </summary>
public static class PaymentValue
{
    private const NumberStyles Styles = NumberStyles.Number;

    /// <summary>Parses a payment cell as a number. False for blank text or anything that isn't a number.</summary>
    public static bool TryParse(string? payment, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(payment))
            return false;

        var text = payment.Trim();
        return decimal.TryParse(text, Styles, CultureInfo.InvariantCulture, out amount)
            || decimal.TryParse(text, Styles, CultureInfo.CurrentCulture, out amount);
    }

    /// <summary>Formats an amount back into a payment cell ("150", "150.5" — no currency, no trailing zeros).</summary>
    public static string Format(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
}
