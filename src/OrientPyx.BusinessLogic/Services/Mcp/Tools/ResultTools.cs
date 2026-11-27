using System.Text.Json.Nodes;
using System.Globalization;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary><c>get_results</c> and <c>get_report</c> — ready documents of the program as data.</summary>
public sealed class ResultTools(McpContext ctx) : IMcpToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = ["get_results", "get_report"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "get_results" => ResultsAsync(args, cancellationToken),
        "get_report" => ReportAsync(args, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private async Task<object?> ResultsAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var competition = ctx.ReadCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var only = args.Strings("groups");
        var limit = args.Int("limit_per_group");
        using var pin = McpContext.Pin(competition, day);
        var data = await ctx.Editor.GetResultProtocolDataAsync(day.Id, cancellationToken);
        var groups = data.Groups
            .Where(g => only.Count == 0 || only.Any(n => TextKey.Same(n.Trim(), g.Name.Trim())))
            .OrderBy(g => g.Order)
            .Select(g => Group(g, limit))
            .ToList();
        if (only.Count > 0 && groups.Count == 0)
            throw new McpToolException($"None of the groups {string.Join(", ", only)} has results on day {day.Number}; groups: {string.Join(", ", data.Groups.Select(g => g.Name))}.");
        return new
        {
            day = day.Number,
            date = day.Date?.ToString("yyyy-MM-dd"),
            extra_columns = data.ExtraColumns.Count == 0 ? null : data.ExtraColumns.Select(c => new { key = c.Key, label = ctx.Ui.Text(c.LabelKey), kind = c.Kind.ToString().ToLowerInvariant() }).ToList(),
            groups,
        };
    }

    private static object Group(ResultProtocolGroup g, int? limit)
    {
        var leader = g.Rows.Where(r => r.Result.Place == 1 && r.Result.RankingTime is not null).Select(r => r.Result.RankingTime).FirstOrDefault();
        // The protocol's own order (ResultProtocolBuilder.BuildPersonalRows): placed finishers by place, then everyone
        // else by name — so limit_per_group keeps the leaders.
        var rows = g.Rows
            .OrderBy(r => r.Result.Place is { } p ? p : int.MaxValue)
            .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
            .AsEnumerable();
        if (limit is > 0)
            rows = rows.Take(limit.Value);
        return new
        {
            group = g.Name,
            discipline = g.Discipline.Value,
            distance_km = g.DistanceKm,
            controls = g.ControlCount,
            time_limit_s = g.TimeLimitSeconds,
            uses_score = g.UsesScore ? true : (bool?)null,
            rank_calculation = g.RankCalculation,
            rows = rows.Select(r => new
            {
                place = r.Result.Place,
                bib = Blank(r.Number),
                participant_id = r.ParticipantId == Guid.Empty ? (Guid?)null : r.ParticipantId,
                full_name = r.FullName,
                birth_year = r.BirthDate?.Year,
                club = Blank(r.ClubName),
                region = Blank(r.RegionName),
                rank = Blank(r.Rank),
                status = McpJson.StatusText(r.Result.Status),
                time_s = r.Result.ResultTime?.TotalSeconds,
                time_text = r.Result.ResultTime is null ? null : ShownTime.Result(r.Result.RankingTime, r.Result.TimeDecimals),
                behind_text = !g.UsesScore && r.Result.Place is > 1 && r.Result.RankingTime is { } t && leader is { } l
                    ? ShownTime.ResultGap(t, l, r.Result.TimeDecimals)
                    : null,
                score = r.Result.Score,
                points = r.Result.Points,
                awarded_rank = r.Result.AwardedRank,
                out_of_competition = r.Result.OutOfCompetition ? true : (bool?)null,
                extra = r.Result.Extra.Count == 0 ? null : r.Result.Extra,
            }).ToList(),
            // Teams in the protocol's standing order, cut by the same limit as the rows.
            relay_teams = g.RelayTeams is null ? null : RelayTeamOrder.Sort(g.RelayTeams, t => t.Result, t => t.Number)
                .Take(limit is > 0 ? limit.Value : int.MaxValue).Select(t => new
            {
                number = t.Number,
                name = t.Name,
                club = Blank(t.ClubName),
                place = t.Result?.Place,
                status = t.Result is null ? null : McpJson.StatusText(t.Result.Status),
                time_s = t.Result?.Time?.TotalSeconds,
                time_text = t.Result?.Time is { } tt ? ShownTime.Result(tt, t.Result.TimeDecimals) : null,
                legs = t.Legs.Select(l => new
                {
                    leg = l.Leg,
                    full_name = l.Runner?.FullName,
                    status = l.Runner is null ? null : McpJson.StatusText(l.Runner.Result.Status),
                    leg_time_s = l.Runner?.Result.Leg?.LegTime?.TotalSeconds,
                }).ToList(),
            }).ToList(),
        };
    }

    private async Task<object?> ReportAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var kind = args.ReqStr("kind").Trim().ToLowerInvariant();
        var competition = ctx.ReadCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        using var pin = McpContext.Pin(competition, day);
        var editor = ctx.Editor;
        object? report = kind switch
        {
            "readiness" => await editor.GetDayReadinessAsync(day.Id, cancellationToken),
            "dashboard" => await editor.GetDashboardAsync(cancellationToken),
            "map_counts" => await editor.GetMapCountsAsync(cancellationToken),
            "summary" => await SummaryAsync(competition, cancellationToken),
            "finish_log" => await editor.GetFinishReadoutLogAsync(cancellationToken, day.Id),
            "relay_teams" => await editor.GetRelayTeamsDataAsync(cancellationToken),
            "number_variants" => await editor.GetNumberVariantsAsync(cancellationToken),
            "entry_fees" => await EntryFeesAsync(cancellationToken),
            "start_list" => await editor.GetStartProtocolDataAsync(day.Id, cancellationToken),
            _ => throw new McpToolException($"Unknown report '{kind}'."),
        };
        return new { report = kind, day = day.Number, data = Shape(kind, McpJson.ToNode(report)) };
    }

    // ── Reports as people read them: clock times instead of the internal base date, a text beside every seconds value,
    // the readiness message itself, no internal ids that only repeat what is next to them.

    private JsonNode? Shape(string kind, JsonNode? node)
    {
        switch (kind)
        {
            case "finish_log":
                foreach (var row in Items(node?["rows"]))
                {
                    Clock(row, "start_time");
                    Clock(row, "finish_time");
                    Clock(row, "resolved_start_time");
                    if (Seconds(row["elapsed"]) is { } elapsed)
                        row["elapsed_text"] = ShownTime.Result(TimeSpan.FromSeconds(elapsed), Int(row["time_decimals"]) ?? 0);
                    if (string.IsNullOrEmpty(row["status_detail"]?.GetValue<string>()))
                    {
                        row.Remove("status_detail");
                        row.Remove("status_detail_kind");
                    }
                }
                break;
            case "dashboard" when node is JsonObject dashboard:
                foreach (var key in new[] { "first_start", "last_start", "last_on_course_start" })
                    if (Seconds(dashboard[key]) is { } s)
                        dashboard[key + "_text"] = McpTime.Clock(TimeSpan.FromSeconds(s));
                break;
            case "start_list":
                foreach (var group in Items(node?["groups"]))
                    foreach (var row in Items(group["rows"]))
                    {
                        if (Seconds(row["start_time"]) is { } s)
                            row["start_time_text"] = McpTime.Clock(TimeSpan.FromSeconds(s));
                        DateOnly(row, "birth_date");
                    }
                break;
            case "map_counts" when node is JsonObject counts:
                counts["days"] = new JsonArray(Items(counts["days"]).Select(d => (JsonNode)new JsonObject
                {
                    ["day"] = d["number"]?.DeepClone(),
                    ["date"] = d["date"] is JsonValue v && DateTimeOffset.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    ["discipline"] = d["default_discipline"]?.DeepClone(),
                }).ToArray());
                break;
            case "readiness":
                foreach (var issue in Items(node?["issues"]))
                {
                    if (issue["message_key"]?.GetValue<string>() is not { Length: > 0 } key)
                        continue;
                    var args = issue["args"] is JsonArray list ? list.Select(a => (object?)a?.ToString()).ToArray() : [];
                    var text = ctx.Ui.Text(key);
                    try
                    {
                        issue["text"] = args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
                    }
                    catch (FormatException)
                    {
                        issue["text"] = text;
                    }
                    issue.Remove("key");
                }
                break;
            case "relay_teams":
                Strip(node, "link_id", "settings_id", "club_id", "region_id");
                break;
        }
        return node;
    }

    private static IEnumerable<JsonObject> Items(JsonNode? array) => array is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    private static double? Seconds(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static int? Int(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    // A stored instant (the read-outs sit on an internal base date) → its clock time «HH:mm:ss».
    private static void Clock(JsonObject row, string key)
    {
        if (row[key] is JsonValue v && v.TryGetValue<string>(out var s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            row[key] = McpTime.Clock(at.TimeOfDay);
    }

    private static void DateOnly(JsonObject row, string key)
    {
        if (row[key] is JsonValue v && v.TryGetValue<string>(out var s)
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            row[key] = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static void Strip(JsonNode? node, params string[] keys)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in keys)
                    o.Remove(key);
                foreach (var (_, child) in o.ToList())
                    Strip(child, keys);
                break;
            case JsonArray a:
                foreach (var child in a)
                    Strip(child, keys);
                break;
        }
    }

    private async Task<object?> SummaryAsync(EventSummary competition, CancellationToken cancellationToken)
    {
        var editor = ctx.Editor;
        var data = await editor.GetSummaryProtocolDataAsync(cancellationToken);
        var settings = await ctx.SummarySettingsAsync(competition, cancellationToken);
        var ranked = ctx.Get<ISummaryProtocolBuilder>().RankGroups(data, settings);
        return new { settings, groups = ranked };
    }

    private async Task<object?> EntryFeesAsync(CancellationToken cancellationToken)
    {
        var roster = await ctx.Editor.GetParticipantRosterAsync(cancellationToken);
        return new
        {
            total = roster.Sum(p => p.TotalEntryFee),
            participants = roster.Count,
            by_club = roster.GroupBy(p => Blank(p.ClubName) ?? "—")
                .Select(g => new { club = g.Key, people = g.Count(), total = g.Sum(p => p.TotalEntryFee) })
                .OrderByDescending(g => g.total).ToList(),
            rows = roster.Select(p => new
            {
                participant_id = p.ParticipantId,
                full_name = p.FullName,
                club = Blank(p.ClubName),
                entry_fee = p.TotalEntryFee,
                payment = Blank(p.Payment),
            }).ToList(),
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
