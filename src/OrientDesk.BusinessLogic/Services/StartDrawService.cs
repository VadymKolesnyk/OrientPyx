using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IStartDrawService"/>. Shuffles each group's members (Fisher–Yates), optionally
/// spreads competitors who share the chosen attribute apart so they don't start consecutively, then lays
/// every start group out from the global start with the fixed interval.
/// </summary>
public sealed class StartDrawService : IStartDrawService
{
    public IReadOnlyList<DrawStartAssignment> Draw(
        IReadOnlyList<IReadOnlyList<DrawGroup>> startGroups,
        TimeSpan globalStart,
        TimeSpan interval,
        DrawSeparationField separation,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(startGroups);

        var random = seed is { } s ? new Random(s) : new Random();
        var result = new List<DrawStartAssignment>();

        foreach (var startGroup in startGroups)
        {
            // Each start lane begins at the global start; members of its groups follow in sequence.
            var offset = 0;
            foreach (var group in startGroup)
            {
                var ordered = DrawGroupOrder(group.Members, separation, random);
                foreach (var member in ordered)
                {
                    var startTime = globalStart + TimeSpan.FromTicks(interval.Ticks * offset);
                    result.Add(new DrawStartAssignment(member.LinkId, startTime));
                    offset++;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Draws the running order for one group: a random shuffle, then (when a separation field is set) a
    /// reordering pass that avoids two consecutive competitors sharing the attribute where possible.
    /// </summary>
    private static IReadOnlyList<DrawParticipant> DrawGroupOrder(
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
        return key.Length > 0 && string.Equals(key, lastKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string KeyOf(DrawParticipant p, DrawSeparationField separation) => separation switch
    {
        DrawSeparationField.Region => p.RegionName?.Trim() ?? string.Empty,
        DrawSeparationField.Club => p.ClubName?.Trim() ?? string.Empty,
        DrawSeparationField.Team => p.Team?.Trim() ?? string.Empty,
        _ => string.Empty,
    };
}
