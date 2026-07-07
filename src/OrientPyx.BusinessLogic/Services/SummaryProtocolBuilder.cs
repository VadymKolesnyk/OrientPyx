using System.Globalization;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ISummaryProtocolBuilder"/>. Aggregates each participant across the counted days and
/// ranks the group per the chosen mode, then formats the two-tier table (leading identity columns, one band of
/// М / Час [ / Очки] per counted day, a trailing «Сума»).
///
/// Ranking:
/// <list type="bullet">
/// <item><b>By points</b> — total = sum of the points earned on each counted day. With <c>RequireAllDays</c>,
/// only members who scored on EVERY counted day are ranked (the rest go поза конкурсом, no place). Without it,
/// everyone is ranked: first by total points (desc), then by the number of counted results (more first), then
/// by the priority day's points (desc); members missing some days fall below those with more results.</item>
/// <item><b>By time</b> — total = sum of the result times on each counted day; only members with a clean result
/// on EVERY counted day are ranked (smaller total wins, ties broken by the priority day's time); everyone else
/// is поза конкурсом. The «Очки» sub-column is omitted.</item>
/// </list>
/// Within поза конкурсом, members are ordered by name. Empty groups (no rankable AND no shown members) are
/// skipped.
/// </summary>
public sealed class SummaryProtocolBuilder : ISummaryProtocolBuilder
{
    public SummaryProtocolDocument Build(SummaryProtocolData data, SummaryProtocolSettings settings, SummaryProtocolLabels labels)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(labels);

        var byPoints = settings.Mode == SummaryMode.ByPoints;

        var countedDays = ResolveCountedDays(data, settings);

        // The tie-break priority day: the configured one (if still counted), else the first counted day.
        var priorityDay = countedDays.FirstOrDefault(d => d.Id == settings.PriorityDayId) ?? countedDays.FirstOrDefault();

        var dayBands = countedDays
            .Select(d => new SummaryDayBand(FormatDayBand(labels.DayBand, d), SubColumns(byPoints, labels)))
            .ToList();

        // The leading columns: the configured visible set in the saved order (fall back to the default layout
        // when none is stored). At least the full-name column is always present.
        var leadingColumns = ResolveLeadingColumns(settings);
        var leading = leadingColumns
            .Select(c => new SummaryColumnSpec(c.ToString(), LeadingCaption(c, labels), BodyWraps(c)))
            .ToList();
        var nameColumnIndex = leadingColumns.IndexOf(SummaryColumn.FullName);
        var subCount = byPoints ? 3 : 2;

        // Per-leaf-column width metadata (leading columns, then each day band's sub-columns, then the total),
        // mirroring the results-protocol width system: a body-wrap flag + a shrink priority (1 = protected;
        // 4 = yields first). The name column is protected; other free-text leading columns yield furthest; the
        // numeric per-day sub-columns + total give way before the name but after nothing wraps unnecessarily.
        var columnBodyWrap = new List<bool>();
        var columnShrinkPriority = new List<int>();
        foreach (var c in leadingColumns)
        {
            columnBodyWrap.Add(BodyWraps(c));
            columnShrinkPriority.Add(LeadingShrinkPriority(c));
        }
        foreach (var band in dayBands)
            foreach (var _ in band.SubColumns)
            {
                columnBodyWrap.Add(false);
                columnShrinkPriority.Add(2);
            }
        columnBodyWrap.Add(false);          // total column
        columnShrinkPriority.Add(2);

        var sections = new List<SummaryProtocolSection>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            var rows = BuildGroupRows(group, leadingColumns, countedDays, priorityDay, settings, byPoints, subCount);
            if (rows.Count == 0)
                continue;
            sections.Add(new SummaryProtocolSection { GroupName = group.Name, Rows = rows });
        }

        var title = settings.Title.Trim().Length > 0 ? settings.Title.Trim() : labels.DefaultTitle;

        return new SummaryProtocolDocument
        {
            Orientation = settings.Orientation,
            CompetitionName = settings.CompetitionName.Trim(),
            Title = title,
            Subtitle = settings.Subtitle.Trim(),
            Venue = settings.Venue.Trim(),
            DateText = settings.DateText.Trim(),
            CompetitionType = settings.CompetitionType.Trim(),
            LeadingColumns = leading,
            NameColumnIndex = nameColumnIndex,
            DayBands = dayBands,
            TotalColumnHeader = labels.Total,
            ColumnBodyWrap = columnBodyWrap,
            ColumnShrinkPriority = columnShrinkPriority,
            Sections = sections,
            Officials = ProtocolOfficialsFactory.Build(
                data.Officials, labels.ChiefJudge, labels.ChiefSecretary, labels.Jury),
            Footer = ProtocolFooterFactory.Build(
                labels.FooterSoftwareName, labels.FooterGeneratedLabel, labels.FooterPageLabel)
        };
    }

    public IReadOnlyList<SummaryRankedGroup> RankGroups(SummaryProtocolData data, SummaryProtocolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);

        var byPoints = settings.Mode == SummaryMode.ByPoints;
        var countedDays = ResolveCountedDays(data, settings);
        var priorityDay = countedDays.FirstOrDefault(d => d.Id == settings.PriorityDayId) ?? countedDays.FirstOrDefault();

        var result = new List<SummaryRankedGroup>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            var (ranked, places, outOfRanking) = RankGroup(group, countedDays, priorityDay, settings, byPoints);

            var entries = new List<SummaryRankedEntry>(ranked.Count + outOfRanking.Count);
            for (var i = 0; i < ranked.Count; i++)
                entries.Add(new SummaryRankedEntry(ranked[i].Member, places[i], TotalText(ranked[i], byPoints)));
            foreach (var a in outOfRanking.OrderBy(a => a.Member.FullName, StringComparer.CurrentCultureIgnoreCase))
                entries.Add(new SummaryRankedEntry(a.Member, null, TotalText(a, byPoints)));

            result.Add(new SummaryRankedGroup(group.Name, group.Order, entries));
        }
        return result;
    }

    // The «Сума» text for an aggregate under the chosen mode: total points (2dp) or total time (hh:mm:ss).
    private static string TotalText(Aggregate a, bool byPoints) =>
        byPoints ? PointsTable.Format(a.TotalPoints) : FormatTime(a.TotalTimeSeconds);

    // The counted days, in the configured on-page order, resolved against the data's day list (a saved day that
    // no longer exists is dropped). Fall back to all days when no selection is stored.
    private static List<SummaryProtocolDay> ResolveCountedDays(SummaryProtocolData data, SummaryProtocolSettings settings)
    {
        var dayById = data.Days.ToDictionary(d => d.Id);
        var countedDays = new List<SummaryProtocolDay>();
        if (settings.Days.Count > 0)
        {
            foreach (var sel in settings.Days)
                if (sel.Counted && dayById.TryGetValue(sel.DayId, out var day))
                    countedDays.Add(day);
        }
        else
        {
            countedDays.AddRange(data.Days.OrderBy(d => d.Number));
        }
        return countedDays;
    }

    // The visible leading columns in the saved order. A summary saved before this feature (no leading-column
    // list) falls back to the default layout; an empty visible set always keeps the full-name column.
    private static List<SummaryColumn> ResolveLeadingColumns(SummaryProtocolSettings settings)
    {
        var source = settings.LeadingColumns is { Count: > 0 }
            ? settings.LeadingColumns
            : SummaryProtocolSettings.DefaultLeadingColumns();
        var visible = source.Where(c => c.Visible).Select(c => c.Column).ToList();
        if (visible.Count == 0)
            visible.Add(SummaryColumn.FullName);
        return visible;
    }

    private static string LeadingCaption(SummaryColumn column, SummaryProtocolLabels labels) => column switch
    {
        SummaryColumn.Sequence => labels.ColSequence,
        SummaryColumn.Number => labels.ColNumber,
        SummaryColumn.FullName => labels.ColFullName,
        SummaryColumn.BirthDate => labels.ColBirthDate,
        SummaryColumn.Region => labels.ColRegion,
        SummaryColumn.Club => labels.ColClub,
        SummaryColumn.Dussh => labels.ColDussh,
        SummaryColumn.Coach => labels.ColCoach,
        SummaryColumn.Rank => labels.ColRank,
        _ => labels.ColFullName
    };

    // The free-text leading columns whose body may wrap (name + place/affiliation text); short codes do not.
    private static bool BodyWraps(SummaryColumn column) => column is
        SummaryColumn.FullName or SummaryColumn.Region or SummaryColumn.Club or
        SummaryColumn.Dussh or SummaryColumn.Coach;

    // The shrink priority of a leading column (1 = never narrowed; 4 = yields first/furthest), mirroring the
    // results protocol: the name is the protected spine; the other wide free-text columns yield furthest; the
    // short-code identity columns (place, number, birth date, qualification) give way moderately.
    private static int LeadingShrinkPriority(SummaryColumn column) => column switch
    {
        SummaryColumn.FullName => 1,
        SummaryColumn.Region or SummaryColumn.Club or SummaryColumn.Dussh or SummaryColumn.Coach => 4,
        _ => 2
    };

    private static IReadOnlyList<string> SubColumns(bool byPoints, SummaryProtocolLabels labels) =>
        byPoints
            ? [labels.SubPlace, labels.SubTime, labels.SubPoints]
            : [labels.SubPlace, labels.SubTime];

    // Builds one group's rows: aggregate each member, rank per the mode, then format each member's row of cells.
    private static List<IReadOnlyList<string>> BuildGroupRows(
        SummaryProtocolGroup group, IReadOnlyList<SummaryColumn> leadingColumns,
        IReadOnlyList<SummaryProtocolDay> countedDays, SummaryProtocolDay? priorityDay,
        SummaryProtocolSettings settings, bool byPoints, int subCount)
    {
        var (ranked, places, outOfRanking) = RankGroup(group, countedDays, priorityDay, settings, byPoints);

        var rows = new List<IReadOnlyList<string>>(ranked.Count + outOfRanking.Count);
        for (var i = 0; i < ranked.Count; i++)
            rows.Add(FormatRow(ranked[i], leadingColumns, countedDays, byPoints, place: places[i]));

        // поза конкурсом — by name, no place (shown «П/К»).
        foreach (var a in outOfRanking.OrderBy(a => a.Member.FullName, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(FormatRow(a, leadingColumns, countedDays, byPoints, place: null));

        return rows;
    }

    // Aggregates one group's members, splits into the ranked set and the поза конкурсом set (per the mode +
    // require-all option), sorts the ranked set, and assigns shared-on-tie 1-based places. Returns the ranked
    // aggregates (parallel to their places) and the leftover поза конкурсом aggregates (unordered). Shared by the
    // document row-builder and the winners printout so both use the exact same ranking.
    private static (List<Aggregate> Ranked, int[] Places, List<Aggregate> OutOfRanking) RankGroup(
        SummaryProtocolGroup group, IReadOnlyList<SummaryProtocolDay> countedDays,
        SummaryProtocolDay? priorityDay, SummaryProtocolSettings settings, bool byPoints)
    {
        var aggregates = group.Members
            .Select(m => BuildAggregate(m, countedDays, priorityDay, byPoints))
            .ToList();

        // Split into the ranked set and the поза конкурсом set, per the mode + option.
        // • By time: only members with a result on every counted day are ranked.
        // • By points + RequireAllDays: same rule.
        // • By points without the option: everyone is ranked (missing days just sort lower).
        var requireAll = !byPoints || settings.RequireAllDays;

        List<Aggregate> ranked;
        List<Aggregate> outOfRanking;
        if (requireAll)
        {
            ranked = aggregates.Where(a => a.CountedResults == countedDays.Count && countedDays.Count > 0).ToList();
            outOfRanking = aggregates.Where(a => !(a.CountedResults == countedDays.Count && countedDays.Count > 0)).ToList();
        }
        else
        {
            // Everyone with at least one counted result is ranked; the truly empty go last поза конкурсом.
            ranked = aggregates.Where(a => a.CountedResults > 0).ToList();
            outOfRanking = aggregates.Where(a => a.CountedResults == 0).ToList();
        }

        SortRanked(ranked, byPoints);

        // Assign 1-based places, sharing a place on a genuine tie (equal on every comparison key).
        var places = AssignPlaces(ranked, byPoints);

        return (ranked, places, outOfRanking);
    }

    // Sorts the ranked set in place: by points desc / time asc, then result-count (points mode without
    // require-all: more results first), then the priority-day value, then name.
    private static void SortRanked(List<Aggregate> ranked, bool byPoints)
    {
        if (byPoints)
        {
            ranked.Sort((a, b) =>
            {
                var c = b.TotalPoints.CompareTo(a.TotalPoints);              // higher total first
                if (c != 0) return c;
                c = b.CountedResults.CompareTo(a.CountedResults);           // more results first
                if (c != 0) return c;
                c = b.PriorityPoints.CompareTo(a.PriorityPoints);          // priority-day points desc
                if (c != 0) return c;
                return string.Compare(a.Member.FullName, b.Member.FullName, StringComparison.CurrentCultureIgnoreCase);
            });
        }
        else
        {
            ranked.Sort((a, b) =>
            {
                var c = a.TotalTimeSeconds.CompareTo(b.TotalTimeSeconds);    // smaller total first
                if (c != 0) return c;
                // priority-day time asc (a missing priority time sorts last)
                c = PriorityTimeKey(a).CompareTo(PriorityTimeKey(b));
                if (c != 0) return c;
                return string.Compare(a.Member.FullName, b.Member.FullName, StringComparison.CurrentCultureIgnoreCase);
            });
        }
    }

    private static double PriorityTimeKey(Aggregate a) => a.PriorityTimeSeconds ?? double.MaxValue;

    // Assigns 1-based places to the already-sorted ranked list, sharing a place when two adjacent entries are
    // equal on the mode's ranking keys (excluding name).
    private static int[] AssignPlaces(List<Aggregate> ranked, bool byPoints)
    {
        var places = new int[ranked.Count];
        for (var i = 0; i < ranked.Count; i++)
        {
            if (i > 0 && RankEqual(ranked[i - 1], ranked[i], byPoints))
                places[i] = places[i - 1];
            else
                places[i] = i + 1;
        }
        return places;
    }

    private static bool RankEqual(Aggregate a, Aggregate b, bool byPoints) => byPoints
        ? a.TotalPoints == b.TotalPoints && a.CountedResults == b.CountedResults && a.PriorityPoints == b.PriorityPoints
        : a.TotalTimeSeconds == b.TotalTimeSeconds && PriorityTimeKey(a) == PriorityTimeKey(b);

    // Aggregates one member across the counted days.
    private static Aggregate BuildAggregate(
        SummaryProtocolParticipant member, IReadOnlyList<SummaryProtocolDay> countedDays,
        SummaryProtocolDay? priorityDay, bool byPoints)
    {
        decimal totalPoints = 0;
        double totalSeconds = 0;
        var counted = 0;
        decimal priorityPoints = 0;
        double? prioritySeconds = null;

        foreach (var day in countedDays)
        {
            if (!member.ResultsByDay.TryGetValue(day.Id, out var r))
                continue;

            if (byPoints)
            {
                if (r.Points is { } pts)
                {
                    totalPoints += pts;
                    counted++;
                    if (priorityDay is { } pd && pd.Id == day.Id)
                        priorityPoints = pts;
                }
            }
            else
            {
                if (r.Status == FinishStatus.Ok && r.ResultTime is { } t && t >= TimeSpan.Zero)
                {
                    totalSeconds += t.TotalSeconds;
                    counted++;
                    if (priorityDay is { } pd && pd.Id == day.Id)
                        prioritySeconds = t.TotalSeconds;
                }
            }
        }

        return new Aggregate(member, totalPoints, totalSeconds, counted, priorityPoints, prioritySeconds);
    }

    // Formats one member's flat cell row: leading identity columns, then each counted day's sub-cells, then Сума.
    private static IReadOnlyList<string> FormatRow(
        Aggregate a, IReadOnlyList<SummaryColumn> leadingColumns,
        IReadOnlyList<SummaryProtocolDay> countedDays, bool byPoints, int? place)
    {
        var cells = new List<string>();

        // Leading — in the configured order.
        foreach (var column in leadingColumns)
            cells.Add(LeadingCell(column, a.Member, place));

        // Per-day sub-cells.
        foreach (var day in countedDays)
        {
            a.Member.ResultsByDay.TryGetValue(day.Id, out var r);
            cells.Add(DayPlaceCell(r));
            cells.Add(DayTimeCell(r));
            if (byPoints)
                cells.Add(DayPointsCell(r));
        }

        // Total.
        cells.Add(byPoints ? PointsTable.Format(a.TotalPoints) : FormatTime(a.TotalTimeSeconds));
        return cells;
    }

    // One leading-column cell value for a member. The place column shows the assigned place, or is left blank
    // when the member is поза конкурсом (no place); the rest are identity fields.
    private static string LeadingCell(SummaryColumn column, SummaryProtocolParticipant m, int? place) => column switch
    {
        SummaryColumn.Sequence => place is { } p ? p.ToString(CultureInfo.InvariantCulture) : string.Empty,
        SummaryColumn.Number => m.Number,
        SummaryColumn.FullName => m.FullName,
        SummaryColumn.BirthDate => m.BirthDate is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty,
        SummaryColumn.Region => m.RegionName,
        SummaryColumn.Club => m.ClubName,
        SummaryColumn.Dussh => m.DusshName,
        SummaryColumn.Coach => m.Coach,
        SummaryColumn.Rank => m.Rank,
        _ => string.Empty
    };

    private static string DayPlaceCell(ParticipantDayResult? r)
    {
        if (r is null)
            return EmDash;
        if (r.Place is { } p)
            return p.ToString(CultureInfo.InvariantCulture);
        return r.OutOfCompetition ? ParticipantDayResult.OutOfCompetitionMark : EmDash;
    }

    private static string DayTimeCell(ParticipantDayResult? r)
    {
        if (r is null)
            return EmDash;
        if (r.Status == FinishStatus.Ok)
            return r.ResultTime is { } t && t >= TimeSpan.Zero ? FormatTimeSpan(t) : EmDash;
        return ShortCode(r.Status) is { Length: > 0 } code ? code : EmDash;
    }

    private static string DayPointsCell(ParticipantDayResult? r) =>
        r?.Points is { } pts ? PointsTable.Format(pts) : "0.00";

    // "00:10:30" — two-digit-hour result time, matching the printed summary sheet.
    private static string FormatTimeSpan(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static string FormatTime(double seconds) => FormatTimeSpan(TimeSpan.FromSeconds(Math.Round(seconds)));

    // The em-dash placeholder shown for a day the participant did not run / has no result.
    private const string EmDash = "–";

    private static string FormatDayBand(string format, SummaryProtocolDay day)
    {
        var dateText = day.Date is { } d ? FormatBandDate(d) : string.Empty;
        return string.Format(CultureInfo.CurrentCulture, format, day.Number, dateText);
    }

    // The day-band date as "30 травня" (day + genitive month name) — matches the printed sheet.
    private static string FormatBandDate(DateTimeOffset date)
    {
        var month = CultureInfo.CurrentCulture.DateTimeFormat.MonthGenitiveNames;
        var m = date.Month - 1;
        var name = m >= 0 && m < month.Length && month[m].Length > 0
            ? month[m]
            : date.ToString("MMMM", CultureInfo.CurrentCulture);
        return $"{date.Day} {name}";
    }

    private static string ShortCode(FinishStatus status) => status switch
    {
        FinishStatus.Ok => "OK",
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => string.Empty
    };

    // The per-member aggregate over the counted days.
    private sealed record Aggregate(
        SummaryProtocolParticipant Member,
        decimal TotalPoints,
        double TotalTimeSeconds,
        int CountedResults,
        decimal PriorityPoints,
        double? PriorityTimeSeconds);
}
