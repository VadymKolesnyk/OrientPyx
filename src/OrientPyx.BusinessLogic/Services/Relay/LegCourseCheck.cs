using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Relay;

/// <summary>
/// Whether a leg number of a group «за номером етапу» (Р-41) has a course the results can use — by the rule the results
/// look it up with: the number's row for the leg naming the team's group or no group, its code one of the group's
/// courses. A row of another group is never applied (the course is then «given in another group»). Shared by the
/// participant tables' red group cell and the day readiness check.
/// </summary>
internal sealed class LegCourseCheck
{
    private readonly IReadOnlySet<Guid> _byBibGroups;
    private readonly ILookup<(int Number, int Leg), LegCourseAssignment> _byNumber;
    private readonly Dictionary<Guid, HashSet<string>> _codes;
    private readonly IReadOnlyDictionary<Guid, string> _groupNames;

    private LegCourseCheck(
        IReadOnlySet<Guid> byBibGroups,
        IEnumerable<LegCourseAssignment> assignments,
        IEnumerable<GroupCourseVariant> variants,
        IReadOnlyDictionary<Guid, string> groupNames)
    {
        _byBibGroups = byBibGroups;
        _byNumber = assignments.ToLookup(a => (a.TeamNumber, a.Leg));
        _codes = variants
            .GroupBy(v => v.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Code.Trim()).ToHashSet(TextKey.Comparer));
        _groupNames = groupNames;
    }

    /// <summary>The relay groups of the day that take their courses «за номером етапу»; empty when none does (then
    /// nothing needs checking and the assignments need not be read).</summary>
    public static IReadOnlySet<Guid> ByBibGroups(
        EventDay day, IEnumerable<GroupDaySettings> settings, Func<DisciplineId, bool> isRelay) =>
        settings
            .Where(s => s.CourseAssignmentMode == RelayCourseMode.ByBib && isRelay(s.DisciplineOverride ?? day.DefaultDiscipline))
            .Select(s => s.GroupId)
            .ToHashSet();

    /// <summary>The check over the day's lists; null when no relay group of the day takes its courses by number.</summary>
    public static LegCourseCheck? For(
        IReadOnlySet<Guid> byBibGroups,
        IEnumerable<LegCourseAssignment> assignments,
        IEnumerable<GroupCourseVariant> variants,
        IEnumerable<Group> groups) =>
        byBibGroups.Count == 0
            ? null
            : new LegCourseCheck(byBibGroups, assignments, variants, groups.ToDictionary(g => g.Id, g => g.Name.Trim()));

    private string GroupName(Guid groupId) => _groupNames.GetValueOrDefault(groupId, string.Empty);

    public RelayLegCourseIssue? IssueOf(RelayTeam team, int leg)
    {
        if (!_byBibGroups.Contains(team.GroupId))
            return null;
        var group = GroupName(team.GroupId);
        if (team.Number is not { } number)
            return new RelayLegCourseIssue(RelayLegCourseIssueKind.NoTeamNumber, group, string.Empty);

        var bib = RelayBib.Format(number, leg);
        var rows = _byNumber[(number, leg)].ToList();
        // The results take the first row of the team's group (or of no group) — a blank one included.
        if (rows.FirstOrDefault(a => a.GroupId is null || a.GroupId == team.GroupId) is { } own)
        {
            var code = own.CourseKey.Trim();
            if (code.Length == 0)
                return new RelayLegCourseIssue(RelayLegCourseIssueKind.NotAssigned, group, bib);
            return _codes.TryGetValue(team.GroupId, out var codes) && codes.Contains(code)
                ? null
                : new RelayLegCourseIssue(RelayLegCourseIssueKind.UnknownCourse, group, bib, code);
        }
        // A row of a group that is gone reads as none given.
        if (rows.FirstOrDefault(a => a.CourseKey.Trim().Length > 0 && a.GroupId is { } g && GroupName(g).Length > 0)
            is { GroupId: { } other })
            return new RelayLegCourseIssue(RelayLegCourseIssueKind.OtherGroup, group, bib, GroupName(other));
        return new RelayLegCourseIssue(RelayLegCourseIssueKind.NotAssigned, group, bib);
    }
}
