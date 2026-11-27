using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using System.Security.Cryptography;
using System.Text;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>assign_groups</c>: the day's participants into groups by rules — birth years (the group's own age window by
/// default), club / region / ДЮСШ / team, the group they have now, an additional field (e.g. «Стать»). The first rule
/// that fits a person wins. A preview with a plan_id first; applied as one undo step.
/// </summary>
public sealed class GroupRuleTools(McpContext ctx) : IMcpToolHandler
{
    private const int PreviewNames = 15;

    private static readonly HashSet<string> RuleKeys = ["group", "birth_year_min", "birth_year_max", "use_age_window", "where"];

    public IReadOnlyCollection<string> Tools { get; } = ["assign_groups"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "assign_groups" => AssignAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private sealed record Rule(GroupDayRow Group, int? MinYear, int? MaxYear, IReadOnlyList<(string Key, IReadOnlyList<string> Values)> Where)
    {
        public bool NeedsYear => MinYear is not null || MaxYear is not null;
    }

    private async Task<object?> AssignAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        if (day.IsLocked)
            throw new McpToolException($"Day {day.Number} is closed for edits (закритий день).");
        // Validate the rules once now (the plan builds them again on apply).
        using (McpContext.Pin(competition, day))
            _ = Rules(args, await ctx.Editor.GetGroupDayRowsAsync(cancellationToken), await ctx.Editor.GetCustomFieldsAsync(cancellationToken));

        Task<McpPlanDraft> Build(CancellationToken t) => DraftAsync(args, day, t);
        if (args.Flag("preview", true))
            return await ctx.Plans.CreateAsync(ctx, "assign_groups", competition, day, Build, cancellationToken);
        McpPlanDraft draft;
        using (McpContext.Pin(competition, day))
            draft = await Build(cancellationToken);
        var write = new McpWrite("assign_groups", McpToolClass.Risky, competition, day, draft.Summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, draft.Apply, call, cancellationToken);
    }

    private static List<Rule> Rules(McpArgs args, IReadOnlyList<GroupDayRow> groups, IReadOnlyList<CustomField> fields)
    {
        var rules = new List<Rule>();
        foreach (var item in args.ReqObjects("rules"))
        {
            foreach (var key in item.Names)
                if (!RuleKeys.Contains(key))
                    throw new McpToolException($"Unknown rule key '{key}'; keys: {string.Join(", ", RuleKeys)}.");
            var group = McpContext.ResolveGroup(groups, item.ReqStr("group"));
            int? min = item.Int("birth_year_min"), max = item.Int("birth_year_max");
            if (min is null && max is null && item.Flag("use_age_window", true))
                (min, max) = (group.MinBirthYear, group.MaxBirthYear);
            if (min is { } a && max is { } b && a > b)
                throw new McpToolException($"Rule for {group.Name}: birth_year_min {a} is after birth_year_max {b}.");
            var where = new List<(string, IReadOnlyList<string>)>();
            if (item.Object("where") is { } w)
                foreach (var key in w.Names)
                {
                    if (key is not ("club" or "region" or "dussh" or "team" or "current_group") && !key.StartsWith("field.", StringComparison.Ordinal))
                        throw new McpToolException($"Unknown 'where' key '{key}'; keys: club, region, dussh, team, current_group, field.<key>.");
                    var target = key;
                    if (key.StartsWith("field.", StringComparison.Ordinal))
                    {
                        // An additional field by its key or its label («field.Стать»); an unknown one would quietly match nobody.
                        var named = key["field.".Length..];
                        var field = fields.FirstOrDefault(f => string.Equals(f.Key, named, StringComparison.OrdinalIgnoreCase))
                            ?? fields.FirstOrDefault(f => TextKey.Same(f.Label, named))
                            ?? throw new McpToolException($"No additional field '{named}'; fields: " +
                                (fields.Count == 0 ? "none" : string.Join(", ", fields.Select(f => $"field.{f.Key} («{f.Label}»)"))) + ".");
                        target = "field." + field.Key;
                    }
                    where.Add((target, w.Strings(key).Select(v => v.Trim()).ToList()));
                }
            rules.Add(new Rule(group, min, max, where));
        }
        return rules;
    }

    private async Task<McpPlanDraft> DraftAsync(McpArgs args, EventDay day, CancellationToken cancellationToken)
    {
        var editor = ctx.Editor;
        var groups = await editor.GetGroupDayRowsAsync(cancellationToken);
        var rules = Rules(args, groups, await editor.GetCustomFieldsAsync(cancellationToken));
        var roster = await editor.GetParticipantRosterAsync(cancellationToken);
        var onlyMissing = args.Flag("only_without_group", true);

        var changes = new List<(ParticipantRosterRow Person, RosterDayCell Cell, Rule Rule)>();
        var unmatched = new List<string>();
        var noYear = new List<string>();
        var relay = new List<string>();
        var already = 0;
        foreach (var person in roster.OrderBy(p => p.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (person.Days.FirstOrDefault(d => d.DayId == day.Id && d.IsMember) is not { } cell)
                continue;
            if (onlyMissing && cell.GroupId is not null)
                continue;
            var rule = rules.FirstOrDefault(r => Fits(r, person, cell));
            if (rule is null)
            {
                if (person.BirthDate is null && rules.Any(r => r.NeedsYear && Where(r, person, cell)))
                    noYear.Add(person.FullName);
                else
                    unmatched.Add(person.FullName);
                continue;
            }
            if (cell.GroupId == rule.Group.GroupId)
            {
                already++;
                continue;
            }
            // A relay runner's group is their team's (Р-22): moved with the team on «Команди», not here.
            if (cell.Relay is not null)
            {
                relay.Add(person.FullName);
                continue;
            }
            changes.Add((person, cell, rule));
        }

        var preview = new
        {
            day = day.Number,
            only_without_group = onlyMissing,
            rules = rules.Select(r => new
            {
                group = r.Group.Name,
                birth_years = r.NeedsYear ? $"{r.MinYear?.ToString() ?? "…"}–{r.MaxYear?.ToString() ?? "…"}" : null,
                where = r.Where.Count == 0 ? null : r.Where.ToDictionary(w => w.Key, w => string.Join(" | ", w.Values)),
                people = changes.Count(c => c.Rule == r),
                examples = changes.Where(c => c.Rule == r).Take(PreviewNames)
                    .Select(c => $"{c.Person.FullName} ({c.Person.BirthDate?.Year.ToString() ?? "?"}){(c.Cell.GroupName is { Length: > 0 } g ? $": {g} →" : string.Empty)}")
                    .ToList(),
            }).ToList(),
            changes = changes.Count,
            already_in_group = already > 0 ? already : (int?)null,
            unmatched = unmatched.Count,
            unmatched_examples = unmatched.Count > 0 ? unmatched.Take(PreviewNames).ToList() : null,
            without_birth_year = noYear.Count > 0 ? noYear.Take(PreviewNames).ToList() : null,
            relay_runners_skipped = relay.Count > 0 ? relay.Take(PreviewNames).ToList() : null,
        };
        var rows = changes.Select(c => new ChangeRow(c.Person.FullName, "group", c.Cell.GroupName is { Length: > 0 } g ? g : "—", c.Rule.Group.Name));
        var summary = ChangeSummary.Of("Mcp.Change.AssignGroups", $"Put {changes.Count} participant(s) of day {day.Number} into groups by rules",
                changes.Count.ToString(), day.Number.ToString())
            .WithRows(rows);
        var plan = changes.Select(c => (c.Person.ParticipantId, c.Rule.Group.GroupId)).ToList();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', plan.Select(p => $"{p.Item1}:{p.Item2}")))));
        return new McpPlanDraft(preview, fingerprint, summary, async t =>
        {
            foreach (var (participantId, groupId) in plan)
                await ctx.Editor.SetParticipantDayGroupAsync(participantId, day.Id, groupId, t);
            return new { assigned = plan.Count, day = day.Number };
        }, Undoable: true);
    }

    private static bool Fits(Rule rule, ParticipantRosterRow person, RosterDayCell cell)
    {
        if (rule.NeedsYear)
        {
            if (person.BirthDate?.Year is not { } year)
                return false;
            if (rule.MinYear is { } min && year < min || rule.MaxYear is { } max && year > max)
                return false;
        }
        return Where(rule, person, cell);
    }

    private static bool Where(Rule rule, ParticipantRosterRow person, RosterDayCell cell) =>
        rule.Where.All(w =>
        {
            var value = w.Key switch
            {
                "club" => person.ClubName,
                "region" => person.RegionName,
                "dussh" => person.DusshName,
                "team" => person.Team,
                "current_group" => cell.GroupName,
                _ => person.Fields.GetValueOrDefault(w.Key["field.".Length..]) ?? string.Empty,
            };
            return w.Values.Any(v => TextKey.Same(v, value) || (v.Length == 0 && value.Trim().Length == 0));
        });
}
