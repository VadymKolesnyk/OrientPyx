namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// The conventional order of groups inside one start group (lane) of the draw, ported from the CourseParser
/// draw modal: the elite (Ч21Е / Ж21Е) starts first, then the veterans 75 → 65 → 55, then the children
/// 12 → 14 → 16, and every other group keeps its original position after them. Within one number the
/// women's group (Ж) precedes the men's (Ч). Groups whose name does not follow the «Ч/Ж + number» pattern
/// (OPEN, M21…) are left in their original order at the end.
/// </summary>
public static class StartGroupOrder
{
    private static readonly string[] SeniorNumbers = ["75", "65", "55"];
    private static readonly string[] KidNumbers = ["12", "14", "16"];

    /// <summary>Stable sort of <paramref name="items"/> by the conventional lane order of their group names.</summary>
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, string> name) =>
        items.Select((item, index) => (Item: item, Key: Key(name(item)), Index: index))
            .OrderBy(x => x.Key.Tier)
            .ThenBy(x => x.Key.Rank)
            .ThenBy(x => x.Key.Sex)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .ToList();

    /// <summary>Sort key of one group name: (tier, rank within the tier, sex); smaller starts earlier.</summary>
    public static (int Tier, int Rank, int Sex) Key(string groupName)
    {
        var label = groupName.Trim();
        if (label.Length < 2)
            return (3, 0, 0);

        var sex = label[0] switch
        {
            'Ж' => 0,
            'Ч' => 1,
            _ => 2,
        };
        if (sex == 2)
            return (3, 0, 0);

        var digits = 0;
        while (1 + digits < label.Length && char.IsAsciiDigit(label[1 + digits]))
            digits++;
        if (digits == 0)
            return (3, 0, 0);

        var number = label.Substring(1, digits);
        var suffix = label[(1 + digits)..];
        if (number == "21" && (suffix == "Е" || suffix == "E"))
            return (0, 0, sex);

        var senior = Array.IndexOf(SeniorNumbers, number);
        if (senior >= 0)
            return (1, senior, sex);

        var kid = Array.IndexOf(KidNumbers, number);
        if (kid >= 0)
            return (2, kid, sex);

        return (3, 0, 0);
    }
}
