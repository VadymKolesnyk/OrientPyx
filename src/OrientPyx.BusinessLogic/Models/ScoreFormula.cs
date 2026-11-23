using System.Globalization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// «Бали» as printed: the total, and when an over-time penalty or a «Бонус» changed it, the sum spelled out from the
/// collected points — «7 (10 − 3)» (penalty), «12 (10 + 2)» (bonus), «9 (10 − 3 + 2)» (both, a negative bonus reads
/// «− 2»); a zero part is left out, a plain number when both are zero. One format for the finish log and the result
/// protocol.
/// </summary>
public static class ScoreFormula
{
    /// <param name="total">The final «Бали» (penalty and bonus already applied).</param>
    /// <param name="penalty">Points the over-time penalty actually took off (0 = none).</param>
    /// <param name="bonus">The judge's points correction folded into the total (0 = none).</param>
    public static string Format(int total, int penalty, int bonus)
    {
        var ci = CultureInfo.InvariantCulture;
        var text = total.ToString(ci);
        if (penalty == 0 && bonus == 0)
            return text;
        var parts = (total - bonus + penalty).ToString(ci);
        if (penalty != 0)
            parts += " − " + penalty.ToString(ci);
        if (bonus != 0)
            parts += (bonus > 0 ? " + " : " − ") + Math.Abs(bonus).ToString(ci);
        return $"{text} ({parts})";
    }

    /// <summary>A computed result's «Бали» (<see cref="ParticipantDayResult.Score"/>, with its
    /// <see cref="ParticipantDayResult.ScorePenalty"/> and <see cref="ParticipantDayResult.Bonus"/>); blank when the
    /// result has no score.</summary>
    public static string Format(ParticipantDayResult result) =>
        result.Score is { } score ? Format(score, result.ScorePenalty ?? 0, result.Bonus ?? 0) : string.Empty;
}
