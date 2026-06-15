using System.Collections.Generic;
using System.Globalization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// How a participant's «Оплата» (payment) text compares to their computed total entry fee.
/// Drives both the payment cell's background tint (<see cref="OrientDesk.Presentation.Behaviors.PaymentHighlight"/>)
/// and the column's "by status" filter, so the two agree by construction — they share
/// <see cref="Classify"/>.
/// </summary>
public enum PaymentStatus
{
    /// <summary>The payment cell is blank/whitespace.</summary>
    Empty,

    /// <summary>A number greater than the total entry fee (overpaid).</summary>
    Over,

    /// <summary>A number less than the total entry fee (underpaid).</summary>
    Under,

    /// <summary>A number equal to the total entry fee (settled) — no tint.</summary>
    Equal,

    /// <summary>The cell holds text that is not a number.</summary>
    NotANumber
}

/// <summary>Classification helpers for <see cref="PaymentStatus"/>.</summary>
public static class PaymentStatusExtensions
{
    // Accept both the invariant ('.') and current-culture decimal separators so a value typed as
    // "150,5" or "150.5" both parse — the total is formatted with InvariantCulture elsewhere, but the
    // user types the payment freely.
    private const NumberStyles Styles = NumberStyles.Number;

    /// <summary>
    /// Classifies a payment cell value against the participant's computed total entry fee.
    /// Blank ⇒ <see cref="PaymentStatus.Empty"/>; unparseable text ⇒ <see cref="PaymentStatus.NotANumber"/>;
    /// otherwise a numeric comparison to <paramref name="totalFee"/>.
    /// </summary>
    public static PaymentStatus Classify(string? payment, decimal totalFee)
    {
        if (string.IsNullOrWhiteSpace(payment))
            return PaymentStatus.Empty;

        var text = payment.Trim();
        if (!decimal.TryParse(text, Styles, CultureInfo.InvariantCulture, out var paid)
            && !decimal.TryParse(text, Styles, CultureInfo.CurrentCulture, out paid))
            return PaymentStatus.NotANumber;

        return paid > totalFee ? PaymentStatus.Over
            : paid < totalFee ? PaymentStatus.Under
            : PaymentStatus.Equal;
    }

    /// <summary>All statuses in display order, for the "by status" filter checkbox list.</summary>
    public static IReadOnlyList<PaymentStatus> All { get; } =
    [
        PaymentStatus.Empty,
        PaymentStatus.Over,
        PaymentStatus.Under,
        PaymentStatus.Equal,
        PaymentStatus.NotANumber
    ];

    /// <summary>Localization key for a status's checkbox label in the filter popup.</summary>
    public static string LabelKey(PaymentStatus status) => status switch
    {
        PaymentStatus.Empty => "Sheet.Filter.PayStatus.Empty",
        PaymentStatus.Over => "Sheet.Filter.PayStatus.Over",
        PaymentStatus.Under => "Sheet.Filter.PayStatus.Under",
        PaymentStatus.Equal => "Sheet.Filter.PayStatus.Equal",
        PaymentStatus.NotANumber => "Sheet.Filter.PayStatus.NotANumber",
        _ => "Sheet.Filter.PayStatus.Empty"
    };
}
