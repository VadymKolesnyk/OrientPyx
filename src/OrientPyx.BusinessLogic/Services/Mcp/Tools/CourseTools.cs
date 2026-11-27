using System.Text.Json;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>upsert_groups</c>, <c>remove_groups</c>, <c>upsert_controls</c>, <c>delete_controls</c>,
/// <c>set_problematic_controls</c> — the day's groups and controls through the «Групи» / «КП» page methods
/// (fresh row → change → save).
/// </summary>
public sealed class CourseTools(McpContext ctx) : IMcpToolHandler
{
    private static readonly HashSet<string> GroupFields =
    [
        "name", "new_name", "course", "distance_km", "discipline", "time_limit_min", "max_late_min", "penalty_per_min",
        "required_controls", "min_birth_year", "max_birth_year", "points_rule", "rank_level", "master_count", "course_setter",
        "course_setter_category", "entry_fee",
    ];

    private static readonly HashSet<string> ControlFields = ["code", "new_code", "type", "points", "lat", "lon", "map_x", "map_y", "disabled"];

    public IReadOnlyCollection<string> Tools { get; } =
        ["upsert_groups", "remove_groups", "upsert_controls", "delete_controls", "set_problematic_controls"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "upsert_groups" => UpsertGroupsAsync(args, call, cancellationToken),
        "remove_groups" => RemoveGroupsAsync(args, call, cancellationToken),
        "upsert_controls" => UpsertControlsAsync(args, call, cancellationToken),
        "delete_controls" => DeleteControlsAsync(args, call, cancellationToken),
        "set_problematic_controls" => ProblematicAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    // ── Groups

    private async Task<object?> UpsertGroupsAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var items = args.ReqObjects("groups");
        IReadOnlyList<GroupDayRow> existing;
        IReadOnlyList<Group> allGroups;
        IReadOnlyList<PointsRuleChoice> rules;
        HashSet<string> dayCodes;
        using (McpContext.Pin(competition, day))
        {
            existing = await ctx.Editor.GetGroupDayRowsAsync(cancellationToken);
            allGroups = await ctx.Editor.GetGroupsAsync(cancellationToken);
            rules = await ctx.Editor.GetPointsRuleChoicesAsync(cancellationToken);
            dayCodes = (await ctx.Editor.GetControlPointsAsync(cancellationToken)).Select(c => TextKey.Of(c.Code.Trim())).ToHashSet();
        }
        var warnings = new List<string>();

        var rows = new List<ChangeRow>();
        var plans = new List<(string Name, bool IsNew, Func<GroupDayRow, GroupDayRow> Apply, bool FeeSet, decimal? Fee)>();
        foreach (var item in items)
        {
            var name = item.ReqStr("name").Trim();
            foreach (var key in item.Names)
                if (!GroupFields.Contains(key))
                    throw new McpToolException($"Unknown group field '{key}'; fields: {string.Join(", ", GroupFields)}.");
            var current = existing.FirstOrDefault(g => TextKey.Same(g.Name.Trim(), name));
            if (item.Str("new_name") is { } newName && newName.Trim().Length > 0
                && allGroups.Any(g => TextKey.Same(g.Name, newName.Trim()) && g.Id != current?.GroupId))
                throw new McpToolException(
                    $"A group '{newName.Trim()}' already exists: renaming '{name}' to it would merge the two groups. Merging is done only in OrientPyx.");

            var apply = GroupApply(item, rules, day);
            var probe = apply(current ?? Blank(name, day));
            rows.AddRange(Diff(current, probe, name));
            // A course naming a control the day does not have can never be run clean: said, not refused (the КП may come later).
            if (item.Has("course") && CourseTokens(probe.CourseOrder).Where(code => !dayCodes.Contains(TextKey.Of(code))).Distinct().ToList() is { Count: > 0 } unknown)
                warnings.Add($"The course of {name} names control(s) day {day.Number} does not have: {string.Join(", ", unknown)} — add them (upsert_controls) or fix the course.");
            var feeSet = item.Has("entry_fee") || (item.Root.TryGetProperty("entry_fee", out var fee) && fee.ValueKind == JsonValueKind.Null);
            plans.Add((name, current is null, apply, feeSet, item.Dec("entry_fee")));
            if (feeSet)
                rows.Add(new ChangeRow(name, "entry_fee", McpValues.Show(allGroups.FirstOrDefault(g => g.Id == current?.GroupId)?.EntryFee), McpValues.Show(item.Dec("entry_fee"))));
        }

        var added = plans.Count(p => p.IsNew);
        var summary = ChangeSummary.Of("Mcp.Change.Groups", $"Groups of day {day.Number}: {added} new, {plans.Count - added} changed",
            day.Number.ToString(), added.ToString(), (plans.Count - added).ToString()).WithRows(rows);
        var write = new McpWrite("upsert_groups", McpToolPolicy.BySize(McpToolClass.Write, 0, plans.Count), competition, day, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            var editor = ctx.Editor;
            var result = new List<object>();
            foreach (var plan in plans)
            {
                var fresh = (await editor.GetGroupDayRowsAsync(token)).FirstOrDefault(g => TextKey.Same(g.Name.Trim(), plan.Name))
                    ?? await editor.AddGroupToDayAsync(plan.Name, token);
                var updated = plan.Apply(fresh);
                if (updated != fresh)
                    await editor.UpdateGroupDayRowAsync(updated, token);
                if (plan.FeeSet)
                    await editor.UpdateGroupEntryFeeAsync(fresh.GroupId, plan.Fee, token);
                result.Add(new { group_id = fresh.GroupId, name = updated.Name, added = plan.IsNew ? true : (bool?)null });
            }
            return new { day = day.Number, groups = result, warnings = warnings.Count == 0 ? null : warnings };
        }, call, cancellationToken);
    }

    // The control codes of a course order or a mixed pattern («S1 31 [2: 32 33] F1»): every token but the «N:» counts.
    private static IEnumerable<string> CourseTokens(string? order) =>
        System.Text.RegularExpressions.Regex.Matches(order ?? string.Empty, @"[^\s\[\]<>:]+(?=[\s\[\]<>]|$)").Select(m => m.Value);

    private static GroupDayRow Blank(string name, EventDay day) =>
        new(Guid.Empty, Guid.Empty, 0, name, string.Empty, null, null, day.DefaultDiscipline, null, null, null);

    private Func<GroupDayRow, GroupDayRow> GroupApply(McpArgs item, IReadOnlyList<PointsRuleChoice> rules, EventDay day)
    {
        var steps = new List<Func<GroupDayRow, GroupDayRow>>();
        void When(string key, Func<JsonElement, Func<GroupDayRow, GroupDayRow>> make)
        {
            if (item.Root.TryGetProperty(key, out var v))
                steps.Add(make(v));
        }

        When("new_name", v => r => r with { Name = McpValues.Text(v)?.Trim() is { Length: > 0 } n ? n : r.Name });
        When("course", v => r => r with { CourseOrder = McpValues.Text(v)?.Trim() ?? string.Empty });
        When("distance_km", v => r => r with { DistanceKm = McpValues.Dec(v, "distance_km") });
        When("discipline", v =>
        {
            var text = McpValues.Text(v);
            DisciplineId? id = string.IsNullOrWhiteSpace(text) ? null : ctx.ResolveDiscipline(text);
            return r => r with { DisciplineOverride = id is { } d && d.Equals(day.DefaultDiscipline) ? null : id };
        });
        When("time_limit_min", v => r => r with { TimeLimitSeconds = Minutes(McpValues.Dec(v, "time_limit_min")) });
        When("max_late_min", v => r => r with { MaxLateSeconds = Minutes(McpValues.Dec(v, "max_late_min")) });
        When("penalty_per_min", v => r => r with { PenaltyPerMinute = McpValues.Dec(v, "penalty_per_min") });
        When("required_controls", v => r => r with { RequiredControlCount = McpValues.Int(v, "required_controls") });
        When("min_birth_year", v => r => r with { MinBirthYear = McpValues.Int(v, "min_birth_year") });
        When("max_birth_year", v => r => r with { MaxBirthYear = McpValues.Int(v, "max_birth_year") });
        When("master_count", v => r => r with { MasterCount = McpValues.Int(v, "master_count") });
        When("course_setter", v => r => r with { CourseSetter = McpValues.Text(v)?.Trim() ?? string.Empty });
        When("course_setter_category", v => r => r with { CourseSetterCategory = McpValues.Text(v)?.Trim() ?? string.Empty });
        When("rank_level", v =>
        {
            var text = McpValues.Text(v)?.Trim() ?? "none";
            var level = Enum.TryParse<GroupRankLevel>(text, true, out var l) ? l : throw new McpToolException("'rank_level' must be none, adult or junior.");
            return r => r with { RankLevel = level };
        });
        When("points_rule", v =>
        {
            var text = McpValues.Text(v)?.Trim();
            Guid? id = null;
            if (!string.IsNullOrEmpty(text))
                id = (rules.FirstOrDefault(c => c.Rule.Id.ToString() == text || TextKey.Same(c.Rule.Name, text))
                    ?? throw new McpToolException($"No points rule '{text}'; rules: {string.Join(", ", rules.Select(c => c.Rule.Name))}.")).Rule.Id;
            return r => r with { PointsRuleId = id };
        });
        return row => steps.Aggregate(row, (r, f) => f(r));
    }

    private static int? Minutes(decimal? minutes) => minutes is { } m ? (int)Math.Round(m * 60) : null;

    private static IEnumerable<ChangeRow> Diff(GroupDayRow? before, GroupDayRow after, string name)
    {
        if (before is null)
        {
            yield return new ChangeRow(name, "add", null, "так");
            before = Blank(name, new EventDay { DefaultDiscipline = after.DayDefaultDiscipline });
        }
        static string? S(object? v) => v is null ? null : McpValues.Show(v);
        (string Field, object? Old, object? New)[] pairs =
        [
            ("name", before.Name, after.Name), ("course", before.CourseOrder, after.CourseOrder), ("distance_km", before.DistanceKm, after.DistanceKm),
            ("discipline", before.DisciplineOverride?.Value, after.DisciplineOverride?.Value), ("time_limit_s", before.TimeLimitSeconds, after.TimeLimitSeconds),
            ("max_late_s", before.MaxLateSeconds, after.MaxLateSeconds), ("penalty_per_min", before.PenaltyPerMinute, after.PenaltyPerMinute),
            ("required_controls", before.RequiredControlCount, after.RequiredControlCount), ("min_birth_year", before.MinBirthYear, after.MinBirthYear),
            ("max_birth_year", before.MaxBirthYear, after.MaxBirthYear), ("points_rule", before.PointsRuleId, after.PointsRuleId),
            ("rank_level", before.RankLevel, after.RankLevel), ("master_count", before.MasterCount, after.MasterCount),
            ("course_setter", before.CourseSetter, after.CourseSetter), ("course_setter_category", before.CourseSetterCategory, after.CourseSetterCategory),
        ];
        foreach (var (field, oldValue, newValue) in pairs)
            if (!Equals(oldValue, newValue))
                yield return new ChangeRow(name, field, S(oldValue), S(newValue));
    }

    private async Task<object?> RemoveGroupsAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var names = args.Strings("groups");
        if (names.Count == 0)
            throw new McpToolException("'groups' must list at least one group.");
        List<GroupDayRow> targets;
        using (McpContext.Pin(competition, day))
        {
            var rows = await ctx.Editor.GetGroupDayRowsAsync(cancellationToken);
            targets = names.Select(n => McpContext.ResolveGroup(rows, n)).DistinctBy(g => g.SettingsId).ToList();
        }
        var summary = ChangeSummary.Of("Mcp.Change.RemoveGroups", $"Take {targets.Count} group(s) off day {day.Number}",
                targets.Count.ToString(), day.Number.ToString())
            .WithRows(targets.Select(g => new ChangeRow(g.Name, $"day {day.Number}", $"{g.ParticipantCount} учасн.", "—")));
        var write = new McpWrite("remove_groups", McpToolPolicy.BySize(McpToolClass.Write, targets.Count, 0), competition, day, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            foreach (var g in targets)
                await ctx.Editor.RemoveGroupFromDayAsync(g.SettingsId, g.GroupId, token);
            return new { removed = targets.Select(g => g.Name).ToList(), day = day.Number };
        }, call, cancellationToken);
    }

    // ── Controls

    private async Task<object?> UpsertControlsAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var items = args.ReqObjects("controls");
        IReadOnlyList<ControlPoint> existing;
        using (McpContext.Pin(competition, day))
            existing = await ctx.Editor.GetControlPointsAsync(cancellationToken);

        var rows = new List<ChangeRow>();
        var plans = new List<(string Code, bool IsNew, Action<ControlPoint> Apply)>();
        foreach (var item in items)
        {
            var code = item.ReqStr("code").Trim();
            foreach (var key in item.Names)
                if (!ControlFields.Contains(key))
                    throw new McpToolException($"Unknown control field '{key}'; fields: {string.Join(", ", ControlFields)}.");
            if (item.Str("new_code") is { } nc && nc.Trim().Length > 0 && !TextKey.Same(nc.Trim(), code)
                && existing.Any(c => TextKey.Same(c.Code.Trim(), nc.Trim())))
                throw new McpToolException($"Control {nc.Trim()} already exists on day {day.Number}.");
            var current = existing.FirstOrDefault(c => TextKey.Same(c.Code.Trim(), code));
            var apply = ControlApply(item);
            var probe = Clone(current) ?? new ControlPoint { Code = code };
            apply(probe);
            if (current is null)
                rows.Add(new ChangeRow(code, "add", null, Describe(probe)));
            else if (Describe(current) != Describe(probe))
                rows.Add(new ChangeRow(code, "control", Describe(current), Describe(probe)));
            plans.Add((code, current is null, apply));
        }

        var added = plans.Count(p => p.IsNew);
        var summary = ChangeSummary.Of("Mcp.Change.Controls", $"Controls of day {day.Number}: {added} new, {plans.Count - added} changed",
            day.Number.ToString(), added.ToString(), (plans.Count - added).ToString()).WithRows(rows);
        var write = new McpWrite("upsert_controls", McpToolPolicy.BySize(McpToolClass.Write, 0, plans.Count), competition, day, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            var editor = ctx.Editor;
            foreach (var plan in plans)
            {
                var point = (await editor.GetControlPointsAsync(token)).FirstOrDefault(c => TextKey.Same(c.Code.Trim(), plan.Code));
                if (point is null)
                {
                    point = await editor.AddControlPointAsync(token);
                    point.Code = plan.Code;
                }
                plan.Apply(point);
                await editor.UpdateControlPointAsync(point, token);
            }
            return new { day = day.Number, controls = plans.Count, added };
        }, call, cancellationToken);
    }

    private static Action<ControlPoint> ControlApply(McpArgs item)
    {
        var steps = new List<Action<ControlPoint>>();
        void When(string key, Func<JsonElement, Action<ControlPoint>> make)
        {
            if (item.Root.TryGetProperty(key, out var v))
                steps.Add(make(v));
        }
        When("new_code", v => c => c.Code = McpValues.Text(v)?.Trim() is { Length: > 0 } n ? n : c.Code);
        When("type", v =>
        {
            var type = (McpValues.Text(v) ?? "control").Trim().ToLowerInvariant() switch
            {
                "start" or "s" => ControlPointType.Start,
                "finish" or "f" => ControlPointType.Finish,
                "control" or "regular" or "" => ControlPointType.Regular,
                var other => throw new McpToolException($"'type' must be control, start or finish (got '{other}')."),
            };
            return c => c.Type = type;
        });
        When("points", v => { var p = McpValues.Int(v, "points"); return c => c.Points = p; });
        When("lat", v => { var d = (double?)McpValues.Dec(v, "lat"); return c => c.Latitude = d; });
        When("lon", v => { var d = (double?)McpValues.Dec(v, "lon"); return c => c.Longitude = d; });
        When("map_x", v => { var d = (double?)McpValues.Dec(v, "map_x"); return c => c.MapX = d; });
        When("map_y", v => { var d = (double?)McpValues.Dec(v, "map_y"); return c => c.MapY = d; });
        When("disabled", v => { var b = McpValues.Bool(v, "disabled"); return c => c.IsDisabled = b; });
        return point => steps.ForEach(s => s(point));
    }

    private static ControlPoint? Clone(ControlPoint? c) => c is null ? null : new ControlPoint
    {
        Id = c.Id, EventDayId = c.EventDayId, Order = c.Order, Code = c.Code, Latitude = c.Latitude, Longitude = c.Longitude,
        MapX = c.MapX, MapY = c.MapY, MapScale = c.MapScale, Type = c.Type, Points = c.Points, IsDisabled = c.IsDisabled, CreatedAt = c.CreatedAt,
    };

    private static string Describe(ControlPoint c) =>
        $"{c.Code} {c.Type.ToString().ToLowerInvariant()}" + (c.Points is { } p ? $" {p} б." : "") +
        (c.Latitude is { } la && c.Longitude is { } lo ? $" ({la:0.#####}, {lo:0.#####})" : "") + (c.IsDisabled ? " проблемний" : "");

    private async Task<object?> DeleteControlsAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var codes = args.Strings("codes");
        List<ControlPoint> targets;
        using (McpContext.Pin(competition, day))
        {
            var existing = await ctx.Editor.GetControlPointsAsync(cancellationToken);
            targets = codes.Select(code => existing.FirstOrDefault(c => TextKey.Same(c.Code.Trim(), code.Trim()))
                ?? throw new McpToolException($"No control {code} on day {day.Number}.")).DistinctBy(c => c.Id).ToList();
        }
        if (targets.Count == 0)
            throw new McpToolException("'codes' must list at least one control.");
        var summary = ChangeSummary.Of("Mcp.Change.DeleteControls", $"Delete {targets.Count} control(s) of day {day.Number}",
            targets.Count.ToString(), day.Number.ToString()).WithRows(targets.Select(c => new ChangeRow(c.Code, "delete", Describe(c), "—")));
        var write = new McpWrite("delete_controls", McpToolPolicy.BySize(McpToolClass.Write, targets.Count, 0), competition, day, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            foreach (var c in targets)
                await ctx.Editor.DeleteControlPointAsync(c.Id, token);
            return new { deleted = targets.Select(c => c.Code).ToList(), day = day.Number };
        }, call, cancellationToken);
    }

    private async Task<object?> ProblematicAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var codes = args.Get("codes") is null ? throw new McpToolException("'codes' is required (an empty array clears).") : args.Strings("codes");
        List<ControlPoint> existing;
        using (McpContext.Pin(competition, day))
            existing = (await ctx.Editor.GetControlPointsAsync(cancellationToken)).ToList();
        var ids = codes.Select(code => existing.FirstOrDefault(c => TextKey.Same(c.Code.Trim(), code.Trim()))?.Id
            ?? throw new McpToolException($"No control {code} on day {day.Number}.")).ToHashSet();
        var rows = existing.Where(c => c.IsDisabled != ids.Contains(c.Id))
            .Select(c => new ChangeRow(c.Code, "проблемний", McpValues.Show(c.IsDisabled), McpValues.Show(ids.Contains(c.Id))));
        var summary = ChangeSummary.Of("Mcp.Change.Problematic",
            $"Problematic controls of day {day.Number}: {(ids.Count == 0 ? "none" : string.Join(", ", codes))}",
            day.Number.ToString(), ids.Count == 0 ? "—" : string.Join(", ", codes)).WithRows(rows);
        var write = new McpWrite("set_problematic_controls", McpToolClass.Write, competition, day, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            var changed = await ctx.Editor.SetProblematicControlsAsync(ids, token);
            return new { day = day.Number, problematic = codes, changed };
        }, call, cancellationToken);
    }
}
