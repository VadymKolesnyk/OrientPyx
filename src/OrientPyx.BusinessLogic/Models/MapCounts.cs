using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// How many participants run in each group on each day of the competition — the numbers handed to the map
/// printer («Кількість карт»). Built by <see cref="Interfaces.ICompetitionEditorService.GetMapCountsAsync"/>:
/// <see cref="Days"/> are the competition's days in number order and every row's <see cref="MapCountRow.Counts"/>
/// is aligned to that list.
/// </summary>
public sealed record MapCountsData(IReadOnlyList<EventDay> Days, IReadOnlyList<MapCountRow> Rows)
{
    public static readonly MapCountsData Empty = new([], []);
}

/// <summary>
/// One group's headcount per day. A <c>null</c> count means the group is not on that day at all (no map is
/// needed); a zero means it is on the day but nobody is entered yet. <see cref="GroupId"/> is null for the
/// synthetic «без групи» row that gathers the day's members with no group assigned — they still need a map,
/// but nobody knows which one, so the row is there to be noticed and resolved before printing.
/// </summary>
public sealed record MapCountRow(Guid? GroupId, string GroupName, IReadOnlyList<int?> Counts);
