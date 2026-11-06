using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Scores a candidate running order of one group against the members' start positions on the previous days
/// (<see cref="DrawParticipant.History"/>). Lower is better; 0 means nothing from the earlier days repeats.
/// The draw generates several random candidates and keeps the one with the lowest score, so these weights
/// only rank candidates — they never override the random shuffle where history is silent.
/// </summary>
internal static class DrawHistoryScore
{
    /// <summary>The same unit opens (or closes) the group as on an earlier day — the heaviest repeat.</summary>
    internal const double RepeatedEdgeWeight = 1000;

    /// <summary>The same «A right after B» pair as on an earlier day: this is what makes small groups look
    /// like yesterday's protocol.</summary>
    internal const double RepeatedPairWeight = 100;

    /// <summary>Small change of position compared with the most recent day (0 for the opposite end,
    /// full weight for the same spot).</summary>
    internal const double CloseToLastWeight = 10;

    /// <summary>Mean position over all days (today included) drifting from the middle, so a unit that started
    /// early twice goes late on the third day.</summary>
    internal const double ImbalanceWeight = 20;

    /// <summary>True when the history can influence the order at all: two or more units and at least one of
    /// them has an earlier day on record.</summary>
    internal static bool Applies(IReadOnlyList<DrawParticipant> members)
        => members.Count >= 2 && members.Any(m => m.History.Count > 0);

    internal static double Score(IReadOnlyList<DrawParticipant> order)
    {
        var size = order.Count;
        if (size < 2)
            return 0;

        var total = 0.0;
        for (var i = 0; i < size; i++)
        {
            var unit = order[i];
            if (unit.History.Count == 0)
                continue;

            var today = (double)i / (size - 1);
            var predecessor = i > 0 ? order[i - 1].HistoryKey : null;
            var sum = today;

            foreach (var day in unit.History)
            {
                sum += day.Normalized;

                // Repeated first / last starter (a group of one that day says nothing about edges).
                if (day.GroupSize >= 2)
                {
                    if (i == 0 && day.Position == 0)
                        total += RepeatedEdgeWeight;
                    if (i == size - 1 && day.Position == day.GroupSize - 1)
                        total += RepeatedEdgeWeight;
                }

                // The same neighbour in front as on that day.
                if (predecessor is not null && day.PredecessorKey is not null
                    && string.Equals(predecessor, day.PredecessorKey, StringComparison.Ordinal))
                    total += RepeatedPairWeight;
            }

            var last = unit.History[^1].Normalized;
            total += CloseToLastWeight * (1 - Math.Abs(today - last));

            var mean = sum / (unit.History.Count + 1);
            total += ImbalanceWeight * Math.Abs(mean - 0.5) * 2;
        }

        return total;
    }
}
