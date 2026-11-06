using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IStartDrawService"/>. Shuffles each group's members (Fisher–Yates), optionally
/// spreads competitors who share the chosen attribute apart so they don't start consecutively, then lays
/// every start group out from the global start with the fixed interval. With <c>considerPreviousDays</c> the
/// shuffle-and-separate step is repeated <see cref="HistoryTrials"/> times and the candidate that repeats the
/// least of the earlier days' positions (<see cref="DrawHistoryScore"/>) is kept — still a random draw, biased
/// away from yesterday.
/// </summary>
public sealed class StartDrawService : IStartDrawService
{
    /// <summary>Candidate orders generated per group when the previous days are taken into account.</summary>
    internal const int HistoryTrials = 200;

    public IReadOnlyList<DrawStartAssignment> Draw(
        IReadOnlyList<IReadOnlyList<DrawGroup>> startGroups,
        TimeSpan globalStart,
        TimeSpan interval,
        DrawSeparationField separation,
        int groupGap = 0,
        int? seed = null,
        bool considerPreviousDays = false)
    {
        ArgumentNullException.ThrowIfNull(startGroups);

        var gap = Math.Max(0, groupGap);
        var random = seed is { } s ? new Random(s) : new Random();
        var result = new List<DrawStartAssignment>();

        foreach (var startGroup in startGroups)
        {
            // Each start lane begins at the global start; members of its groups follow in sequence, with an
            // optional run of empty start slots (groupGap) inserted after each group before the next begins.
            var offset = 0;
            for (var gi = 0; gi < startGroup.Count; gi++)
            {
                var group = startGroup[gi];
                var ordered = DrawGroupOrder(group.Members, separation, random, considerPreviousDays);
                foreach (var member in ordered)
                {
                    var startTime = globalStart + TimeSpan.FromTicks(interval.Ticks * offset);
                    result.Add(new DrawStartAssignment(member.LinkId, startTime));
                    offset++;
                }

                // Leave the gap only between groups, not after the last one in the lane.
                if (ordered.Count > 0 && gi < startGroup.Count - 1)
                    offset += gap;
            }
        }

        return result;
    }

    public IReadOnlyList<DrawStartAssignment> DrawClassic(
        IReadOnlyList<ClassicDrawGroup> groups,
        DrawSeparationField separation,
        int? seed = null,
        bool considerPreviousDays = false)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var random = seed is { } s ? new Random(s) : new Random();
        var result = new List<DrawStartAssignment>();

        // Each group is independent: drawn from its own start with its own interval.
        foreach (var g in groups)
        {
            var ordered = DrawGroupOrder(g.Group.Members, separation, random, considerPreviousDays);
            var slot = 0;
            foreach (var member in ordered)
            {
                var startTime = g.Start + TimeSpan.FromTicks(g.Interval.Ticks * slot);
                result.Add(new DrawStartAssignment(member.LinkId, startTime));
                slot++;
            }
        }

        return result;
    }

    /// <summary>
    /// Draws the running order for one group: a random shuffle, then (when a separation field is set) a
    /// reordering pass that avoids two consecutive competitors sharing the attribute where possible. When the
    /// previous days count and any member has a history, that is repeated <see cref="HistoryTrials"/> times and
    /// the lowest-scoring candidate wins (the first of equals, so equally good orders stay random). Without
    /// history the result is exactly the single-pass draw — the same seed gives the same order as before.
    /// </summary>
    private static IReadOnlyList<DrawParticipant> DrawGroupOrder(
        IReadOnlyList<DrawParticipant> members,
        DrawSeparationField separation,
        Random random,
        bool considerPreviousDays)
    {
        if (!considerPreviousDays || !DrawHistoryScore.Applies(members))
            return DrawGroupOrderOnce(members, separation, random);

        IReadOnlyList<DrawParticipant>? best = null;
        var bestScore = double.PositiveInfinity;
        for (var trial = 0; trial < HistoryTrials; trial++)
        {
            var candidate = DrawGroupOrderOnce(members, separation, random);
            var score = DrawHistoryScore.Score(candidate);
            if (score < bestScore)
            {
                best = candidate;
                bestScore = score;
                if (score == 0)
                    break;
            }
        }

        return best!;
    }

    private static IReadOnlyList<DrawParticipant> DrawGroupOrderOnce(
        IReadOnlyList<DrawParticipant> members,
        DrawSeparationField separation,
        Random random)
    {
        var shuffled = members.ToList();
        Shuffle(shuffled, random);

        if (separation == DrawSeparationField.None || shuffled.Count < 3)
            return shuffled;

        return SeparateConsecutive(shuffled, separation);
    }

    /// <summary>In-place Fisher–Yates shuffle.</summary>
    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Greedily rebuilds the order so that no two adjacent competitors share the separation key when that
    /// can be avoided. Walks the shuffled list; whenever the next competitor would clash with the one just
    /// placed, it pulls forward the nearest following competitor with a different key (the rule "insert the
    /// next competitor between them"). If every remaining competitor clashes (e.g. a key dominates the rest
    /// of the group), the original order is kept — the constraint is satisfied as far as it physically can be.
    /// The shuffle stays the tie-break, so the result is still random for equal-key competitors.
    /// </summary>
    private static IReadOnlyList<DrawParticipant> SeparateConsecutive(
        List<DrawParticipant> shuffled,
        DrawSeparationField separation)
    {
        var remaining = new LinkedList<DrawParticipant>(shuffled);
        var ordered = new List<DrawParticipant>(shuffled.Count);
        string? lastKey = null;

        while (remaining.First is not null)
        {
            var pick = remaining.First;

            // A blank key never clashes; otherwise look for the first competitor whose key differs from
            // the previously placed one.
            if (lastKey is not null && KeyMatches(pick!.Value, separation, lastKey))
            {
                var alt = pick!.Next;
                while (alt is not null && KeyMatches(alt.Value, separation, lastKey))
                    alt = alt.Next;
                if (alt is not null)
                    pick = alt; // pull the nearest non-clashing competitor forward
                // else: all remaining clash — accept the clash and place the head anyway.
            }

            ordered.Add(pick!.Value);
            lastKey = KeyOf(pick!.Value, separation);
            remaining.Remove(pick!);
        }

        return ordered;
    }

    private static bool KeyMatches(DrawParticipant p, DrawSeparationField separation, string lastKey)
    {
        var key = KeyOf(p, separation);
        // A blank key is treated as "no group", so it never clashes with anything.
        return key.Length > 0 && TextKey.Same(key, lastKey);
    }

    private static string KeyOf(DrawParticipant p, DrawSeparationField separation) => separation switch
    {
        DrawSeparationField.Region => p.RegionName?.Trim() ?? string.Empty,
        DrawSeparationField.Club => p.ClubName?.Trim() ?? string.Empty,
        DrawSeparationField.Team => p.Team?.Trim() ?? string.Empty,
        _ => string.Empty,
    };
}
