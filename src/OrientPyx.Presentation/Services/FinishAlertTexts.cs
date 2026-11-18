using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The words of the finish alerts — the in-window toast and the Windows notification — built in one place, so the
/// «Зчитка на фініші» page and the examples in «Налаштування сповіщень» say exactly the same thing.
/// </summary>
public static class FinishAlertTexts
{
    /// <summary>«Заберіть прокатний чіп» / «Чіп 8001023 — №57 Петренко Іван (Ч21)».</summary>
    public static (string Title, string Message) CollectChip(
        ILocalizationService localization, string chip, string? number, string? fullName, string? group)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(number))
            parts.Add("№" + number);
        if (!string.IsNullOrWhiteSpace(fullName))
            parts.Add(fullName);
        if (!string.IsNullOrWhiteSpace(group))
            parts.Add($"({group})");

        return (localization.Get("FinishRead.CollectToast.Title"),
            string.Format(localization.Get("FinishRead.CollectToast.Message"), chip, string.Join(" ", parts)));
    }

    /// <summary>«Невідомий чіп 8001099» / «Оберіть учасника в OrientPyx — доти наступні зчитки чекають.»</summary>
    public static (string Title, string Message) UnknownChip(ILocalizationService localization, string chip)
        => (string.Format(localization.Get("FinishAlerts.Notify.Unknown.Title"), chip),
            localization.Get("FinishAlerts.Notify.Unknown.Message"));
}
