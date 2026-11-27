using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>find_duplicates</c> (two entries that may be one person: the same name, the name in another order, a typo, the
/// same ФСОУ code) and <c>merge_participants</c> (the editor's merge: the merged entry goes, its days move over, the
/// kept one's blanks are filled — one undo step).
/// </summary>
public sealed class DuplicateTools(McpContext ctx) : IMcpToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = ["find_duplicates", "merge_participants"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "find_duplicates" => FindAsync(args, cancellationToken),
        "merge_participants" => MergeAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    // ── find_duplicates

    private async Task<object?> FindAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var competition = ctx.ReadCompetition(args);
        var limit = Math.Clamp(args.Int("limit") ?? 50, 1, 500);
        IReadOnlyList<ParticipantRosterRow> roster;
        EventDay? day = null;
        using (McpContext.Pin(competition, null))
            roster = await ctx.Editor.GetParticipantRosterAsync(cancellationToken);
        if (args.Int("day") is not null)
            day = await ctx.DayAsync(competition, args, cancellationToken);

        var pairs = Candidates(roster);
        var shown = pairs
            .Where(p => day is null || OnDay(p.A, day.Id) || OnDay(p.B, day.Id))
            .OrderBy(p => p.Rank)
            .ThenBy(p => p.A.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new
        {
            pairs = shown.Count,
            candidates = shown.Take(limit).Select(p => new
            {
                reasons = p.Reasons.OrderBy(r => r).ToList(),
                likely_namesakes = p.YearsDiffer ? true : (bool?)null,
                both_on_days = BothOn(p.A, p.B) is { Count: > 0 } both ? both : null,
                a = Card(p.A),
                b = Card(p.B),
            }).ToList(),
            note = shown.Count == 0
                ? "No likely duplicates."
                : "Candidates only — ask the user which pairs are one person (birth years that differ usually mean namesakes). " +
                  "merge_participants keeps one entry; on a day both run, the merged one's entry is dropped.",
        };
    }

    private sealed record Pair(ParticipantRosterRow A, ParticipantRosterRow B, HashSet<string> Reasons)
    {
        public bool YearsDiffer => A.BirthDate is { } a && B.BirthDate is { } b && a.Year != b.Year;

        // Strong reasons and matching birth years first; namesakes last.
        public int Rank => (YearsDiffer ? 10 : 0) + (Reasons.Contains("same_fsou_code") || Reasons.Contains("same_name") ? 0 : 1);
    }

    private static List<Pair> Candidates(IReadOnlyList<ParticipantRosterRow> roster)
    {
        var pairs = new Dictionary<(Guid, Guid), Pair>();
        void Add(ParticipantRosterRow x, ParticipantRosterRow y, string reason)
        {
            if (x.ParticipantId == y.ParticipantId)
                return;
            var (a, b) = x.ParticipantId.CompareTo(y.ParticipantId) < 0 ? (x, y) : (y, x);
            if (!pairs.TryGetValue((a.ParticipantId, b.ParticipantId), out var pair))
                pairs[(a.ParticipantId, b.ParticipantId)] = pair = new Pair(a, b, []);
            pair.Reasons.Add(reason);
        }
        void AllPairs(IEnumerable<IGrouping<string, ParticipantRosterRow>> groups, string reason, bool differentNames = false)
        {
            foreach (var g in groups)
            {
                var list = g.ToList();
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                        if (!differentNames || Norm(list[i].FullName) != Norm(list[j].FullName))
                            Add(list[i], list[j], reason);
            }
        }

        var named = roster.Where(p => Norm(p.FullName).Length > 0).ToList();
        AllPairs(named.GroupBy(p => Norm(p.FullName)).Where(g => g.Count() > 1), "same_name");
        AllPairs(named.GroupBy(p => string.Join(' ', Norm(p.FullName).Split(' ').Order(StringComparer.Ordinal)))
            .Where(g => g.Count() > 1 && g.Select(p => Norm(p.FullName)).Distinct().Count() > 1), "name_order", differentNames: true);
        AllPairs(roster.Where(p => p.FsouCode.Trim().Length > 0).GroupBy(p => TextKey.Of(p.FsouCode)).Where(g => g.Count() > 1), "same_fsou_code");

        // A typo: names a letter or two apart, among people whose names start alike and whose birth years do not differ.
        foreach (var bucket in named.GroupBy(p => Norm(p.FullName)[..Math.Min(2, Norm(p.FullName).Length)]))
        {
            var list = bucket.ToList();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var (x, y) = (list[i], list[j]);
                    if (x.BirthDate is { } bx && y.BirthDate is { } by && bx.Year != by.Year)
                        continue;
                    var (nx, ny) = (Norm(x.FullName), Norm(y.FullName));
                    if (nx == ny || Math.Min(nx.Length, ny.Length) < 6)
                        continue;
                    var allowed = Math.Min(nx.Length, ny.Length) >= 10 ? 2 : 1;
                    if (Math.Abs(nx.Length - ny.Length) <= allowed && Distance(nx, ny, allowed) <= allowed)
                        Add(x, y, "similar_name");
                }
        }
        return pairs.Values.ToList();
    }

    // The name as it is matched: TextKey (case, spaces, look-alikes), apostrophes dropped, hyphens as spaces.
    private static string Norm(string name) =>
        TextKey.Of(new string((name ?? string.Empty).Where(c => c is not ('\'' or '’' or 'ʼ' or '`' or '‘')).Select(c => c == '-' ? ' ' : c).ToArray()));

    // Levenshtein distance, stopping once it is over max.
    private static int Distance(string a, string b, int max)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, cur[j]);
            }
            if (best > max)
                return best;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static bool OnDay(ParticipantRosterRow p, Guid dayId) => p.Days.Any(d => d.DayId == dayId && d.IsMember);

    private static List<int> BothOn(ParticipantRosterRow a, ParticipantRosterRow b) =>
        a.Days.Where(d => d.IsMember && OnDay(b, d.DayId)).Select(d => d.DayNumber).Order().ToList();

    private static object Card(ParticipantRosterRow p) => new
    {
        id = p.ParticipantId,
        full_name = p.FullName,
        bib = Blank(p.Number),
        birth_date = p.BirthDate?.ToString("yyyy-MM-dd"),
        club = Blank(p.ClubName),
        region = Blank(p.RegionName),
        fsou_code = Blank(p.FsouCode),
        days = p.Days.Where(d => d.IsMember).Select(d => new { day = d.DayNumber, group = Blank(d.GroupName), chip = Blank(d.Chip) }).ToList(),
    };

    // ── merge_participants

    private async Task<object?> MergeAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var items = args.ReqObjects("merges");
        IReadOnlyList<ParticipantRosterRow> roster;
        using (McpContext.Pin(competition, null))
            roster = await ctx.Editor.GetParticipantRosterAsync(cancellationToken);

        var plan = new List<(ParticipantRosterRow Keep, ParticipantRosterRow Merge)>();
        var used = new HashSet<Guid>();
        foreach (var item in items)
        {
            var keep = McpContext.ResolveParticipant(roster, item.ReqStr("keep"));
            var merge = McpContext.ResolveParticipant(roster, item.ReqStr("merge"));
            if (keep.ParticipantId == merge.ParticipantId)
                throw new McpToolException($"'{item.ReqStr("keep")}' and '{item.ReqStr("merge")}' are the same entry.");
            if (!used.Add(keep.ParticipantId) || !used.Add(merge.ParticipantId))
                throw new McpToolException("Each person may appear in one merge only (merge a chain of entries one pair after another).");
            plan.Add((keep, merge));
        }

        var rows = plan.Select(p => new ChangeRow(p.Keep.FullName, "merge",
            $"{p.Merge.FullName} ({Blank(p.Merge.Number) ?? "—"}, {p.Merge.BirthDate?.Year.ToString() ?? "?"}, {Blank(p.Merge.ClubName) ?? "—"})",
            BothOn(p.Keep, p.Merge) is { Count: > 0 } both ? $"merged; its entry on day {string.Join(", ", both)} dropped" : "merged")).ToList();
        var summary = ChangeSummary.Of("Mcp.Change.Merge", $"Merge {plan.Count} duplicate pair(s): " + string.Join("; ", plan.Take(3).Select(p => $"{p.Merge.FullName} → {p.Keep.FullName}")), plan.Count.ToString())
            .WithRows(rows);
        var cls = McpToolPolicy.BySize(McpToolClass.Write, plan.Count, 0);
        var write = new McpWrite("merge_participants", cls, competition, null, summary, Undoable: true,
            ParticipantId: plan.Count == 1 ? plan[0].Keep.ParticipantId : null);
        return await ctx.Writer.RunAsync(write, async t =>
        {
            var done = new List<object>();
            foreach (var (keep, merge) in plan)
            {
                var r = await ctx.Editor.MergeParticipantsAsync(keep.ParticipantId, merge.ParticipantId, t);
                done.Add(new
                {
                    kept = new { id = keep.ParticipantId, full_name = keep.FullName },
                    deleted = new { id = merge.ParticipantId, full_name = merge.FullName },
                    days_moved = r.MovedDays,
                    days_dropped = r.DroppedDays.Count > 0 ? r.DroppedDays : null,
                    filled = r.FilledFields.Count > 0 ? r.FilledFields : null,
                    discounts_added = r.DiscountsAdded > 0 ? r.DiscountsAdded : (int?)null,
                });
            }
            return new { merged = done.Count, merges = done };
        }, call, cancellationToken);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
