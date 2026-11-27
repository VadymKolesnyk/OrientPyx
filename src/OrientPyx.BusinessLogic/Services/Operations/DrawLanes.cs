using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Operations;

/// <summary>
/// How the day's groups are arranged into start lanes (коридори) for the draw — the «Жеребкування» page and the
/// assistant's <c>draw_start_times</c> share it. Groups without members are left out; groups sharing a first control
/// never split across lanes.
/// </summary>
public static class DrawLanes
{
    /// <summary>
    /// <paramref name="target"/> balanced lanes: the groups clustered by first control (indivisible), heaviest cluster
    /// first into the lightest lane (LPT); heaviest lane first, inside a lane the conventional order (<see cref="StartGroupOrder"/>).
    /// </summary>
    public static List<List<DrawGroup>> Distribute(IReadOnlyList<DrawGroup> groups, int target)
    {
        var atoms = new Dictionary<string, (List<DrawGroup> Items, int Sum)>();
        foreach (var group in groups)
        {
            if (group.Members.Count == 0)
                continue;
            var key = group.FirstControl;
            if (!atoms.TryGetValue(key, out var atom))
                atom = ([], 0);
            atom.Items.Add(group);
            atoms[key] = (atom.Items, atom.Sum + group.Members.Count);
        }

        var atomList = atoms.Values.ToList();
        if (atomList.Count == 0)
            return [[]];

        var k = Math.Max(1, Math.Min(target, atomList.Count));
        atomList.Sort((a, b) => b.Sum.CompareTo(a.Sum));
        var buckets = Enumerable.Range(0, k).Select(_ => (Items: new List<DrawGroup>(), Sum: 0)).ToList();
        foreach (var atom in atomList)
        {
            var best = 0;
            for (var i = 1; i < buckets.Count; i++)
                if (buckets[i].Sum < buckets[best].Sum)
                    best = i;
            buckets[best].Items.AddRange(atom.Items);
            buckets[best] = (buckets[best].Items, buckets[best].Sum + atom.Sum);
        }

        buckets.Sort((a, b) => b.Sum.CompareTo(a.Sum));
        return buckets.Select(b => StartGroupOrder.Sort(b.Items, g => g.Name).ToList()).ToList();
    }

    /// <summary>
    /// The lanes of a saved arrangement: ids no longer on the day are skipped, and a group the arrangement doesn't mention
    /// (added since) lands in the first lane so nothing is lost.
    /// </summary>
    public static List<List<DrawGroup>> Restore(IReadOnlyList<DrawGroup> groups, IReadOnlyList<DrawLaneSettings> lanes)
    {
        var byId = groups.Where(g => g.Members.Count > 0).ToDictionary(g => g.GroupId);
        var placed = new HashSet<Guid>();
        var result = new List<List<DrawGroup>>();
        foreach (var lane in lanes)
        {
            var column = new List<DrawGroup>();
            foreach (var id in lane.GroupIds)
                if (byId.TryGetValue(id, out var group) && placed.Add(id))
                    column.Add(group);
            result.Add(column);
        }
        if (result.Count == 0)
            result.Add([]);
        foreach (var group in groups)
            if (group.Members.Count > 0 && placed.Add(group.GroupId))
                result[0].Add(group);
        return result;
    }
}
