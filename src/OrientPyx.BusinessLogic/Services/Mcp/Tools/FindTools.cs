using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Text;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary><c>find</c>, <c>get_participant</c>, <c>get_splits</c>.</summary>
public sealed class FindTools(McpContext ctx) : IMcpToolHandler
{
    private static readonly string[] AllKinds = ["participant", "group", "club", "region", "dussh", "control", "chip", "team"];

    public IReadOnlyCollection<string> Tools { get; } = ["find", "get_participant", "get_splits"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "find" => FindAsync(args, cancellationToken),
        "get_participant" => ParticipantAsync(args, cancellationToken),
        "get_splits" => SplitsAsync(args, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private async Task<object?> FindAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var text = args.ReqStr("text").Trim();
        var kinds = args.Strings("kinds").Select(k => k.ToLowerInvariant()).ToHashSet();
        if (kinds.Count == 0)
            kinds = AllKinds.ToHashSet();
        var limit = Math.Clamp(args.Int("limit") ?? 20, 1, 200);
        var competition = ctx.ReadCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var variants = TextSearch.Variants(text);
        bool Hit(string? value) => !string.IsNullOrWhiteSpace(value) && TextSearch.ContainsAny(value, variants);

        using var pin = McpContext.Pin(competition, day);
        var editor = ctx.Editor;
        var found = new Dictionary<string, object>();

        if (kinds.Contains("participant") || kinds.Contains("chip"))
        {
            var roster = await editor.GetParticipantRosterAsync(cancellationToken);
            if (kinds.Contains("participant"))
            {
                var people = roster
                    .Where(p => Hit(p.FullName) || TextKey.Same(p.Number.Trim(), text) || p.Days.Any(d => d.Chip.Trim().Length > 0 && TextKey.Same(d.Chip.Trim(), text))
                        || p.Days.Any(d => d.DayId == day.Id && d.IsMember && McpContext.IsLegNumber(d, text)))
                    .OrderByDescending(p => TextKey.Same(p.Number.Trim(), text) || TextKey.Same(p.FullName, text)
                        || p.Days.Any(d => d.DayId == day.Id && McpContext.IsLegNumber(d, text)))
                    .Take(limit)
                    .Select(p => Card(p, day))
                    .ToList();
                found["participants"] = people;
            }
            if (kinds.Contains("chip"))
            {
                var rental = await editor.GetRentalChipsAsync(cancellationToken);
                var holders = await editor.GetRentalChipHoldersAsync(cancellationToken);
                found["rental_chips"] = rental
                    .Where(c => Hit(c.Number) || Hit(c.Note))
                    .Take(limit)
                    .Select(c => new { number = c.Number, note = Blank(c.Note), holder = holders.GetValueOrDefault(c.Number) })
                    .ToList();
            }
        }

        if (kinds.Contains("group"))
        {
            var groups = await editor.GetGroupDayRowsAsync(cancellationToken);
            found["groups"] = groups.Where(g => Hit(g.Name)).Take(limit).Select(g => new
            {
                group_id = g.GroupId,
                name = g.Name,
                day = day.Number,
                course = Blank(g.CourseOrder),
                distance_km = g.DistanceKm,
                discipline = (g.DisciplineOverride ?? g.DayDefaultDiscipline).Value,
                participants = g.ParticipantCount,
            }).ToList();
        }

        if (kinds.Contains("club"))
            found["clubs"] = (await editor.GetClubsAsync(cancellationToken)).Where(c => Hit(c.Name)).Take(limit)
                .Select(c => new { club_id = c.Id, name = c.Name }).ToList();
        if (kinds.Contains("region"))
            found["regions"] = (await editor.GetRegionsAsync(cancellationToken)).Where(c => Hit(c.Name)).Take(limit)
                .Select(c => new { region_id = c.Id, name = c.Name }).ToList();
        if (kinds.Contains("dussh"))
            found["dussh"] = (await editor.GetDusshesAsync(cancellationToken)).Where(c => Hit(c.Name)).Take(limit)
                .Select(c => new { dussh_id = c.Id, name = c.Name }).ToList();
        if (kinds.Contains("control"))
            found["controls"] = (await editor.GetControlPointsAsync(cancellationToken))
                .Where(c => TextKey.Same(c.Code.Trim(), text) || Hit(c.Code)).Take(limit)
                .Select(c => new { code = c.Code.Trim(), day = day.Number, type = c.Type.ToString().ToLowerInvariant(), points = c.Points, disabled = c.IsDisabled })
                .ToList();

        if (kinds.Contains("team"))
        {
            var relay = await editor.GetRelayTeamsDataAsync(cancellationToken);
            var groupNames = relay.Groups.ToDictionary(g => g.GroupId, g => g.GroupName);
            found["teams"] = relay.Teams
                .Where(t => Hit(t.Name) || (t.Number is { } n && TextKey.Same(n.ToString(), text)))
                .OrderByDescending(t => t.Number is { } n && TextKey.Same(n.ToString(), text))
                .Take(limit)
                .Select(t => new
                {
                    team_id = t.Id,
                    number = t.Number,
                    name = Blank(t.Name),
                    group = groupNames.GetValueOrDefault(t.GroupId),
                    day = day.Number,
                    status = t.Result is null ? null : McpJson.StatusText(t.Result.Status),
                    place = t.Result?.Place,
                    legs = t.Legs.Select(l => new { leg = l.Leg, bib = RelayTeamNumbers.Bib(t.Number, l.Leg), participant_id = l.ParticipantId, full_name = Blank(l.FullName) }).ToList(),
                })
                .ToList();
        }

        return found;
    }

    private static object Card(ParticipantRosterRow p, EventDay day)
    {
        var cell = p.Days.FirstOrDefault(d => d.DayId == day.Id);
        return new
        {
            participant_id = p.ParticipantId,
            full_name = p.FullName,
            bib = Blank(p.Number),
            birth_year = p.BirthDate?.Year,
            club = Blank(p.ClubName),
            region = Blank(p.RegionName),
            day = day.Number,
            in_day = cell?.IsMember ?? false,
            group = Blank(cell?.GroupName),
            chip = Blank(cell?.Chip),
            relay = cell?.Relay is { } r ? new { team = r.TeamNumber, team_name = Blank(r.TeamName), leg = r.Leg, number = Blank(r.Bib) } : null,
            status = cell is { IsMember: true } ? McpJson.StatusText(cell.Result.Status) : null,
            place = cell?.Result.Place,
        };
    }

    private async Task<object?> ParticipantAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var competition = ctx.ReadCompetition(args);
        var onlyDay = args.Int("day");
        var withSplits = args.Flag("include_splits", true);
        var days = await ctx.DaysAsync(competition, cancellationToken);
        using var pin = McpContext.Pin(competition, null);
        var editor = ctx.Editor;
        var roster = await editor.GetParticipantRosterAsync(cancellationToken);
        var p = McpContext.ResolveParticipant(roster, args.ReqStr("participant"), onlyDay is { } dn ? days.FirstOrDefault(d => d.Number == dn)?.Id : null);
        var discounts = (await editor.GetEntryFeeDiscountsAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        var fields = await editor.GetCustomFieldsAsync(cancellationToken);

        var perDay = new List<object>();
        foreach (var cell in p.Days.OrderBy(d => d.DayNumber))
        {
            if (onlyDay is { } n && cell.DayNumber != n)
                continue;
            var day = days.FirstOrDefault(d => d.Id == cell.DayId);
            object? splits = null;
            if (withSplits && cell.IsMember && cell.LinkId is { } linkId && day is not null && cell.Result.HasReadout)
            {
                using (McpContext.Pin(competition, day))
                {
                    var data = await editor.GetAnalyticsDayDataAsync(day.Id, cancellationToken);
                    if (data?.SplitsByLink.GetValueOrDefault(linkId) is { } view)
                        splits = SplitsJson(view);
                }
            }
            perDay.Add(new
            {
                day = cell.DayNumber,
                in_day = cell.IsMember,
                group = Blank(cell.GroupName),
                chip = Blank(cell.Chip),
                start_time = cell.StartTime is { } st ? McpTime.Clock(st) : null,
                out_of_competition = cell.OutOfCompetition ? true : (bool?)null,
                bonus = cell.Bonus,
                payment = Blank(cell.Payment),
                result = cell.IsMember ? ResultJson(cell.Result) : null,
                relay = cell.Relay is { } relay ? McpJson.ToNode(relay) : null,
                splits,
            });
        }

        return new
        {
            participant_id = p.ParticipantId,
            full_name = p.FullName,
            bib = Blank(p.Number),
            birth_date = p.BirthDate?.ToString("yyyy-MM-dd"),
            club = Blank(p.ClubName),
            region = Blank(p.RegionName),
            dussh = Blank(p.DusshName),
            rank = Blank(p.Rank),
            coach = Blank(p.Coach),
            representative = Blank(p.Representative),
            team = Blank(p.Team),
            fsou_code = Blank(p.FsouCode),
            fsou_member = p.IsFsouMember,
            note = Blank(p.Note),
            checked_in = p.CheckedIn,
            pays_raised_fee = p.PaysRaisedFee,
            payment = Blank(p.Payment),
            entry_fee = p.TotalEntryFee,
            discounts = p.SelectedDiscountIds.Select(id => discounts.GetValueOrDefault(id)).Where(n => n is not null).ToList(),
            fields = fields
                .Where(f => p.Fields.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v))
                .ToDictionary(f => f.Key, f => new { label = f.Label, value = p.Fields[f.Key] }),
            days = perDay,
        };
    }

    private async Task<object?> SplitsAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var competition = ctx.ReadCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        using var pin = McpContext.Pin(competition, day);
        var editor = ctx.Editor;
        var p = McpContext.ResolveParticipant(await editor.GetParticipantRosterAsync(cancellationToken), args.ReqStr("participant"), day.Id);
        var cell = p.Days.FirstOrDefault(d => d.DayId == day.Id);
        if (cell is not { IsMember: true, LinkId: { } linkId })
            throw new McpToolException($"{p.FullName} does not take part on day {day.Number}.");
        var data = await editor.GetAnalyticsDayDataAsync(day.Id, cancellationToken);
        if (data?.SplitsByLink.GetValueOrDefault(linkId) is not { } view)
            return new { participant_id = p.ParticipantId, full_name = p.FullName, day = day.Number, read_out = false, note = "No read-out of the chip on this day." };
        return new
        {
            participant_id = p.ParticipantId,
            full_name = p.FullName,
            day = day.Number,
            group = Blank(cell.GroupName),
            result = ResultJson(cell.Result),
            splits = SplitsJson(view),
        };
    }

    /// <summary>A result as the assistant sees it: status, times (seconds + text), place, «Бали», «Очки».</summary>
    internal static object ResultJson(ParticipantDayResult r) => new
    {
        status = McpJson.StatusText(r.Status),
        computed_status = r.Override is not null ? McpJson.StatusText(r.Computed) : null,
        time_s = r.ResultTime?.TotalSeconds,
        time_text = r.ResultTime is null ? null : ShownTime.Result(r.RankingTime, r.TimeDecimals),
        place = r.Place,
        out_of_competition = r.OutOfCompetition ? true : (bool?)null,
        score = r.Score,
        score_penalty = r.ScorePenalty is > 0 ? r.ScorePenalty : null,
        bonus = r.Bonus,
        points = r.Points,
        awarded_rank = r.AwardedRank,
        missed = r.Missed.Count == 0 ? null : r.Missed,
        detail = string.IsNullOrWhiteSpace(r.StatusDetail) ? null : r.StatusDetail,
        unranked_reason = r.UnrankedReason,
        start = r.ActualStart is { } s ? McpTime.Clock(s.TimeOfDay) : null,
        finish = r.FinishTime is { } f ? McpTime.Clock(f.TimeOfDay) : null,
        has_readout = r.HasReadout,
        extra = r.Extra.Count == 0 ? null : r.Extra,
    };

    internal static object SplitsJson(SplitsView v) => new
    {
        layout = v.Layout.ToString().ToLowerInvariant(),
        variant = v.VariantCode.Length == 0 ? null : v.VariantCode,
        course = v.PrescribedPattern.Length == 0 ? null : v.PrescribedPattern,
        visited = v.VisitedCount,
        expected = v.ExpectedCount,
        points = v.HasPoints ? v.TotalPoints : (int?)null,
        passage = v.Passage.Select(p => new
        {
            code = p.Kind switch { PassageKind.Start => "S", PassageKind.Finish => "F", _ => p.Code },
            on_course = p.OnCourse,
            clock = p.Time is { } t ? McpTime.Clock(t.TimeOfDay) : null,
            leg_s = (p.DisplayLeg ?? p.Leg)?.TotalSeconds,
            elapsed_s = p.Elapsed?.TotalSeconds,
            leg_km = p.DisplayLegKm ?? p.LegKm,
            points = p.Points,
        }).ToList(),
        missed = v.Expected.Where(e => !e.Taken && !e.Ignored).Select(e => e.Code).ToList() is { Count: > 0 } m ? m : null,
        disabled = v.DisabledControls.Count == 0 ? null : v.DisabledControls,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
