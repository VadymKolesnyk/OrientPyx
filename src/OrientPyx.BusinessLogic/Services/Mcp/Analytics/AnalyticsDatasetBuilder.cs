using System.Collections.Concurrent;
using System.Globalization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Results;

namespace OrientPyx.BusinessLogic.Services.Mcp.Analytics;

/// <summary>
/// Builds the analytics tables of one competition (plan §5.2) for the chosen days, only through
/// <see cref="ICompetitionEditorService"/> — the results and passages are those the protocols and the split export show;
/// nothing is computed again here except the per-leg statistics. Each day is read once per builder.
/// </summary>
public sealed class AnalyticsDatasetBuilder
{
    private readonly McpContext _ctx;
    private readonly EventSummary _competition;
    private readonly IReadOnlyList<EventDay> _days;
    private readonly ConcurrentDictionary<Guid, Task<AnalyticsDayData?>> _dayData = new();
    private Task<IReadOnlyList<ParticipantRosterRow>>? _roster;
    private Task<IReadOnlyList<CustomField>>? _fields;
    private Task<IReadOnlyList<RentalChip>>? _rentalChips;

    public AnalyticsDatasetBuilder(McpContext ctx, EventSummary competition, IReadOnlyList<EventDay> days)
    {
        _ctx = ctx;
        _competition = competition;
        _days = days;
    }

    private ICompetitionEditorService Editor => _ctx.Editor;

    /// <summary>The columns of a table for this competition (participants gains a field_* column per additional field).</summary>
    public async Task<IReadOnlyList<AnalyticsColumn>> ColumnsAsync(string table, CancellationToken cancellationToken)
    {
        var info = AnalyticsSchema.Find(table) ?? throw new McpToolException($"Unknown table '{table}'.");
        if (!info.Name.Equals("participants", StringComparison.Ordinal))
            return info.Columns;
        using (McpContext.Pin(_competition, null))
        {
            var fields = await FieldsAsync(cancellationToken);
            return info.Columns
                .Concat(fields.Select(f => new AnalyticsColumn(AnalyticsSchema.FieldColumn(f.Key), FieldType(f.Kind),
                    $"Additional field «{f.Label}» ({f.Kind.ToString().ToLowerInvariant()}).")))
                .GroupBy(c => c.Name).Select(g => g.First())
                .ToList();
        }
    }

    public async Task<AnalyticsTable> BuildAsync(string table, CancellationToken cancellationToken)
    {
        using var pin = McpContext.Pin(_competition, null);
        var columns = await ColumnsAsync(table, cancellationToken);
        var rows = new TableRows(columns);
        switch (table.ToLowerInvariant())
        {
            case "competition": await CompetitionAsync(rows, cancellationToken); break;
            case "days": await DaysAsync(rows, cancellationToken); break;
            case "groups": await ForEachDayAsync(rows, Groups, cancellationToken); break;
            case "controls": await ForEachDayAsync(rows, Controls, cancellationToken); break;
            case "course_legs": await ForEachDayAsync(rows, CourseLegs, cancellationToken); break;
            case "participants": await ParticipantsAsync(rows, cancellationToken); break;
            case "entries": await EntriesAsync(rows, cancellationToken); break;
            case "results": await ForEachDayAsync(rows, Results, cancellationToken); break;
            case "legs": await ForEachDayAsync(rows, Legs, cancellationToken); break;
            case "punches": await ForEachDayAsync(rows, Punches, cancellationToken); break;
            case "readouts": await ForEachDayAsync(rows, Readouts, cancellationToken); break;
            case "relay_teams": await ForEachDayAsync(rows, RelayTeams, cancellationToken); break;
            case "relay_legs": await ForEachDayAsync(rows, RelayLegs, cancellationToken); break;
            case "clubs":
                var clubCounts = await Editor.GetClubParticipantCountsAsync(cancellationToken);
                foreach (var c in await Editor.GetClubsAsync(cancellationToken))
                    rows.Add(("club_id", c.Id), ("name", c.Name), ("participants", clubCounts.GetValueOrDefault(c.Id)));
                break;
            case "regions":
                var regionCounts = await Editor.GetRegionParticipantCountsAsync(cancellationToken);
                foreach (var r in await Editor.GetRegionsAsync(cancellationToken))
                    rows.Add(("region_id", r.Id), ("name", r.Name), ("participants", regionCounts.GetValueOrDefault(r.Id)));
                break;
            case "dussh":
                var dusshCounts = await Editor.GetDusshParticipantCountsAsync(cancellationToken);
                foreach (var d in await Editor.GetDusshesAsync(cancellationToken))
                    rows.Add(("dussh_id", d.Id), ("name", d.Name), ("participants", dusshCounts.GetValueOrDefault(d.Id)));
                break;
            case "rental_chips":
                foreach (var chip in await RentalChipsAsync(cancellationToken))
                    rows.Add(("number", chip.Number), ("note", chip.Note));
                break;
            case "custom_fields":
                foreach (var f in await FieldsAsync(cancellationToken))
                    rows.Add(("key", f.Key), ("column", AnalyticsSchema.FieldColumn(f.Key)), ("label", f.Label),
                        ("kind", f.Kind.ToString().ToLowerInvariant()), ("source", f.Source));
                break;
            case "discounts":
                foreach (var d in await Editor.GetEntryFeeDiscountsAsync(cancellationToken))
                    rows.Add(("discount_id", d.Id), ("name", d.Name), ("percent", d.Percent), ("applies_to_chip_rental", d.AppliesToChipRental),
                        ("fsou_member", d.IsFsouMemberDiscount));
                break;
            case "chip_prices":
                foreach (var p in await Editor.GetChipPriceOverridesAsync(cancellationToken))
                    rows.Add(("note", p.Note), ("price_per_day", p.PricePerDay));
                break;
            case "points_rules":
                foreach (var choice in await Editor.GetPointsRuleChoicesAsync(cancellationToken))
                    rows.Add(("rule_id", choice.Rule.Id), ("name", choice.Rule.Name), ("from_competition", choice.IsCompetitionCopy));
                break;
            default:
                throw new McpToolException($"Unknown table '{table}'.");
        }
        return new AnalyticsTable(table.ToLowerInvariant(), columns, rows.Rows);
    }

    // ── Loading

    private Task<AnalyticsDayData?> DayDataAsync(EventDay day, CancellationToken cancellationToken) =>
        _dayData.GetOrAdd(day.Id, _ => Editor.GetAnalyticsDayDataAsync(day.Id, cancellationToken));

    private Task<IReadOnlyList<ParticipantRosterRow>> RosterAsync(CancellationToken cancellationToken) =>
        _roster ??= Editor.GetParticipantRosterAsync(cancellationToken);

    private Task<IReadOnlyList<CustomField>> FieldsAsync(CancellationToken cancellationToken) =>
        _fields ??= Editor.GetCustomFieldsAsync(cancellationToken);

    private Task<IReadOnlyList<RentalChip>> RentalChipsAsync(CancellationToken cancellationToken) =>
        _rentalChips ??= Editor.GetRentalChipsAsync(cancellationToken);

    private async Task ForEachDayAsync(TableRows rows, Action<TableRows, AnalyticsDayData> fill, CancellationToken cancellationToken)
    {
        foreach (var day in _days)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DayDataAsync(day, cancellationToken) is { } data)
                fill(rows, data);
        }
    }

    // ── Tables

    private async Task CompetitionAsync(TableRows rows, CancellationToken cancellationToken)
    {
        var info = await Editor.GetInfoAsync(cancellationToken);
        var allDays = await _ctx.DaysAsync(_competition, cancellationToken);
        rows.Add(
            ("name", info?.Name ?? _competition.Name), ("identifier", _competition.Identifier), ("venue", info?.Venue),
            ("organisation", info?.Organisation), ("start_date", Date(info?.StartDate)), ("end_date", Date(info?.EndDate)),
            ("day_count", allDays.Count), ("course_setter", info?.CourseSetter), ("chief_judge", info?.ChiefJudge),
            ("chief_secretary", info?.ChiefSecretary), ("jury", info?.Jury), ("roster_enabled", info?.RosterEnabled),
            ("payment_per_day", info?.PaymentPerDay), ("raised_fee_enabled", info?.RaisedFeeEnabled),
            ("raised_fee_amount", info?.RaisedFeeAmount), ("chip_rental_price_per_day", info?.ChipRentalPricePerDay));
    }

    private async Task DaysAsync(TableRows rows, CancellationToken cancellationToken)
    {
        foreach (var day in _days)
        {
            var data = await DayDataAsync(day, cancellationToken);
            rows.Add(("day", day.Number), ("day_id", day.Id), ("date", Date(day.Date)), ("venue", day.Venue),
                ("discipline", day.DefaultDiscipline.Value), ("locked", day.IsLocked),
                ("participants", data?.Snapshot.Links.Count ?? 0));
        }
    }

    private static void Groups(TableRows rows, AnalyticsDayData data)
    {
        var s = data.Snapshot;
        var groups = s.Groups.ToDictionary(g => g.Id);
        var counts = s.Links.Where(l => l.GroupId is not null).GroupBy(l => l.GroupId!.Value).ToDictionary(g => g.Key, g => g.Count());
        foreach (var gs in s.GroupDaySettings.OrderBy(x => x.Order))
        {
            groups.TryGetValue(gs.GroupId, out var g);
            rows.Add(("day", data.Day.Number), ("group_id", gs.GroupId), ("group_name", g?.Name),
                ("discipline", (gs.DisciplineOverride ?? data.Day.DefaultDiscipline).Value), ("course", gs.CourseOrder),
                ("distance_km", gs.DistanceKm), ("required_controls", gs.RequiredControlCount), ("time_limit_s", gs.TimeLimitSeconds),
                ("max_late_s", gs.MaxLateSeconds), ("penalty_per_min", gs.PenaltyPerMinute), ("min_birth_year", g?.MinBirthYear),
                ("max_birth_year", g?.MaxBirthYear), ("points_rule_id", gs.PointsRuleId), ("rank_level", Lower(gs.RankLevel)),
                ("master_count", gs.MasterCount), ("leg_count", gs.LegCount), ("course_mode", Snake(gs.CourseAssignmentMode.ToString())),
                ("course_setter", gs.CourseSetter), ("entry_fee", g?.EntryFee), ("participants", counts.GetValueOrDefault(gs.GroupId)));
        }
    }

    private static void Controls(TableRows rows, AnalyticsDayData data)
    {
        foreach (var c in data.Snapshot.ControlPoints.OrderBy(c => c.Order))
            rows.Add(("day", data.Day.Number), ("code", c.Code.Trim()), ("type", ControlType(c.Type)), ("points", c.Points),
                ("disabled", c.IsDisabled), ("lat", c.Latitude), ("lon", c.Longitude), ("map_x", c.MapX), ("map_y", c.MapY));
    }

    private void CourseLegs(TableRows rows, AnalyticsDayData data)
    {
        var s = data.Snapshot;
        var groups = s.Groups.ToDictionary(g => g.Id);
        var coords = s.ControlPoints
            .GroupBy(c => c.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => new GeoPoint(g.First().Latitude, g.First().Longitude), StringComparer.OrdinalIgnoreCase);
        var start = s.ControlPoints.FirstOrDefault(c => c.Type == ControlPointType.Start);
        var finish = s.ControlPoints.FirstOrDefault(c => c.Type == ControlPointType.Finish);
        var startFinish = CourseCodes.StartFinishCodesOf(s.ControlPoints);
        var distance = _ctx.Find<ICourseDistanceCalculator>();

        foreach (var gs in s.GroupDaySettings.OrderBy(x => x.Order))
        {
            var codes = CourseCodes.CourseOrderCodes(gs.CourseOrder).Where(c => !startFinish.Contains(c)).ToList();
            if (codes.Count == 0)
                continue;
            var chain = new List<(string Code, GeoPoint Point)>();
            if (start is not null)
                chain.Add(("S", new GeoPoint(start.Latitude, start.Longitude)));
            chain.AddRange(codes.Select(c => (c, coords.GetValueOrDefault(c))));
            if (finish is not null)
                chain.Add(("F", new GeoPoint(finish.Latitude, finish.Longitude)));
            groups.TryGetValue(gs.GroupId, out var g);
            for (var i = 1; i < chain.Count; i++)
            {
                var (a, b) = (chain[i - 1], chain[i]);
                decimal? km = a.Point.HasCoordinates && b.Point.HasCoordinates && distance is not null
                    ? distance.TotalKilometres([a.Point, b.Point])
                    : null;
                rows.Add(("day", data.Day.Number), ("group_id", gs.GroupId), ("group_name", g?.Name), ("leg_no", i),
                    ("from_code", a.Code), ("to_code", b.Code), ("length_km", km));
            }
        }
    }

    private async Task ParticipantsAsync(TableRows rows, CancellationToken cancellationToken)
    {
        var roster = await RosterAsync(cancellationToken);
        var fields = await FieldsAsync(cancellationToken);
        var discounts = (await Editor.GetEntryFeeDiscountsAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        foreach (var p in roster)
        {
            var values = new List<(string, object?)>
            {
                ("participant_id", p.ParticipantId), ("bib", Blank(p.Number)), ("full_name", p.FullName),
                ("birth_date", Date(p.BirthDate)), ("birth_year", p.BirthDate?.Year), ("club", Blank(p.ClubName)),
                ("region", Blank(p.RegionName)), ("dussh", Blank(p.DusshName)), ("rank", Blank(p.Rank)), ("coach", Blank(p.Coach)),
                ("representative", Blank(p.Representative)), ("team", Blank(p.Team)), ("fsou_code", Blank(p.FsouCode)),
                ("fsou_member", p.IsFsouMember), ("note", Blank(p.Note)), ("checked_in", p.CheckedIn),
                ("pays_raised_fee", p.PaysRaisedFee), ("entry_fee", p.TotalEntryFee), ("payment", Blank(p.Payment)),
                ("discounts", string.Join(", ", p.SelectedDiscountIds.Select(id => discounts.GetValueOrDefault(id)).Where(n => n is not null))),
                ("days", p.Days.Count(d => d.IsMember)),
            };
            foreach (var f in fields)
                values.Add((AnalyticsSchema.FieldColumn(f.Key), FieldValue(f, p.Fields.GetValueOrDefault(f.Key))));
            rows.Add(values.ToArray());
        }
    }

    private async Task EntriesAsync(TableRows rows, CancellationToken cancellationToken)
    {
        var rental = (await RentalChipsAsync(cancellationToken)).Select(c => c.Number.Trim()).ToHashSet(TextKey.Comparer);
        await ForEachDayAsync(rows, (r, data) =>
        {
            var s = data.Snapshot;
            var groups = s.Groups.ToDictionary(g => g.Id, g => g.Name);
            var teams = s.RelayTeams.ToDictionary(t => t.Id);
            foreach (var l in s.Links)
            {
                var team = l.RelayTeamId is { } tid ? teams.GetValueOrDefault(tid) : null;
                r.Add(("day", data.Day.Number), ("participant_id", l.ParticipantId), ("group_id", l.GroupId),
                    ("group_name", l.GroupId is { } gid ? groups.GetValueOrDefault(gid) : null), ("chip", Blank(l.Chip)),
                    ("rental_chip", l.Chip.Trim().Length > 0 && rental.Contains(l.Chip.Trim())),
                    ("start_time_s", l.StartTime?.TotalSeconds), ("start_time_text", l.StartTime is { } st ? McpTime.Clock(st) : null),
                    ("out_of_competition", l.OutOfCompetition),
                    ("status_override", l.ResultStatusOverride is { } so ? McpJson.StatusText(so) : null), ("bonus", l.Bonus),
                    ("payment", Blank(l.Payment)), ("pays_raised_fee", l.PaysRaisedFee), ("relay_team_id", l.RelayTeamId),
                    ("relay_team_number", team?.Number), ("leg", l.Leg));
            }
        }, cancellationToken);
    }

    private static void Results(TableRows rows, AnalyticsDayData data)
    {
        var s = data.Snapshot;
        var groups = s.Groups.ToDictionary(g => g.Id, g => g.Name);
        var winners = WinnerTimes(data);
        foreach (var l in s.Links)
        {
            var r = data.ResultsByLink.GetValueOrDefault(l.Id) ?? ParticipantDayResult.Empty;
            double? behind = null;
            if (r.Status == FinishStatus.Ok && r.Place is not null && r.RankingTime is { } t
                && l.GroupId is { } gid && winners.TryGetValue(gid, out var best))
                behind = (t - best).TotalSeconds;
            var variant = data.SplitsByLink.TryGetValue(l.Id, out var sv) && sv.VariantCode.Length > 0
                ? sv.VariantCode
                : r.Leg?.CourseCode;
            rows.Add(("day", data.Day.Number), ("participant_id", l.ParticipantId),
                ("group_name", l.GroupId is { } g2 ? groups.GetValueOrDefault(g2) : null),
                ("status", McpJson.StatusText(r.Status)), ("computed_status", McpJson.StatusText(r.Computed)), ("has_readout", r.HasReadout),
                ("start_clock_s", Clock(r.ActualStart)), ("finish_clock_s", Clock(r.FinishTime)), ("time_s", r.ResultTime?.TotalSeconds),
                ("adjusted_time_s", r.AdjustedTime?.TotalSeconds),
                ("time_text", r.ResultTime is null ? null : ShownTime.Result(r.RankingTime, r.TimeDecimals)),
                ("place", r.Place), ("behind_s", behind), ("out_of_competition", r.OutOfCompetition || l.OutOfCompetition),
                ("score", r.Score), ("score_gross", r.ScoreGross), ("score_penalty", r.ScorePenalty), ("bonus", r.Bonus),
                ("points", r.Points), ("awarded_rank", r.AwardedRank), ("course_variant", variant),
                ("missed", r.Missed.Count == 0 ? null : string.Join(",", r.Missed)), ("unranked_reason", r.UnrankedReason),
                ("relay_team_id", r.Team?.TeamId), ("leg", r.Leg?.Leg),
                ("extra_json", r.Extra.Count == 0 ? null : McpJson.Serialize(r.Extra)));
        }
    }

    // The ranking time of each group's winner (place 1), for «behind».
    private static Dictionary<Guid, TimeSpan> WinnerTimes(AnalyticsDayData data)
    {
        var best = new Dictionary<Guid, TimeSpan>();
        foreach (var l in data.Snapshot.Links)
        {
            if (l.GroupId is not { } gid || data.ResultsByLink.GetValueOrDefault(l.Id) is not { Place: 1, RankingTime: { } t })
                continue;
            if (!best.TryGetValue(gid, out var cur) || t < cur)
                best[gid] = t;
        }
        return best;
    }

    private static void Legs(TableRows rows, AnalyticsDayData data)
    {
        var s = data.Snapshot;
        var groups = s.Groups.ToDictionary(g => g.Id, g => g.Name);
        var legs = new List<LegRow>();
        foreach (var l in s.Links)
        {
            if (!data.SplitsByLink.TryGetValue(l.Id, out var view) || view.Passage.Count == 0)
                continue;
            var result = data.ResultsByLink.GetValueOrDefault(l.Id);
            var variant = view.VariantCode.Length > 0 ? view.VariantCode : result?.Leg?.CourseCode ?? string.Empty;
            var previous = "S";
            var n = 0;
            foreach (var p in view.Passage)
            {
                if (p.Kind == PassageKind.Start)
                {
                    previous = "S";
                    continue;
                }
                var code = p.Kind == PassageKind.Finish ? "F" : p.Code.Trim();
                n++;
                var leg = p.DisplayLeg ?? p.Leg;
                var km = p.DisplayLegKm ?? p.LegKm;
                legs.Add(new LegRow(l.ParticipantId, l.GroupId, variant, n, previous, code, p.OnCourse || p.Kind == PassageKind.Finish,
                    leg?.TotalSeconds, p.Elapsed?.TotalSeconds, km, p.DisplayPace ?? p.PaceSecondsPerKm));
                previous = code;
            }
        }

        // The statistics of a leg: runners of the same group and variant on the same control pair, counting legs.
        var stats = legs
            .Where(x => x.OnCourse && x.LegSeconds is > 0)
            .GroupBy(x => (x.GroupId, x.Variant, x.From, x.To))
            .ToDictionary(g => g.Key, g => g.Select(x => x.LegSeconds!.Value).OrderBy(v => v).ToList());

        foreach (var x in legs)
        {
            int? place = null;
            double? bestS = null, median = null, ratio = null, behind = null;
            var runners = 0;
            if (x.OnCourse && x.LegSeconds is > 0 && stats.TryGetValue((x.GroupId, x.Variant, x.From, x.To), out var times))
            {
                runners = times.Count;
                bestS = times[0];
                median = Median(times);
                place = times.FindIndex(v => v >= x.LegSeconds.Value - 1e-9) + 1;
                ratio = median > 0 ? x.LegSeconds / median : null;
                behind = x.LegSeconds - bestS;
            }
            rows.Add(("day", data.Day.Number), ("participant_id", x.ParticipantId),
                ("group_name", x.GroupId is { } gid ? groups.GetValueOrDefault(gid) : null),
                ("course_variant", x.Variant.Length == 0 ? null : x.Variant), ("leg_no", x.No), ("from_code", x.From), ("to_code", x.To),
                ("on_course", x.OnCourse), ("leg_s", x.LegSeconds), ("cumulative_s", x.Elapsed), ("leg_km", x.Km),
                ("pace_s_per_km", x.Pace), ("leg_place", place), ("leg_best_s", bestS), ("leg_median_s", median),
                ("ratio_to_median", ratio is { } q ? Math.Round(q, 4) : null), ("behind_leg_best_s", behind),
                ("runners_on_leg", runners == 0 ? null : runners));
        }
    }

    private sealed record LegRow(
        Guid ParticipantId, Guid? GroupId, string Variant, int No, string From, string To, bool OnCourse,
        double? LegSeconds, double? Elapsed, decimal? Km, double? Pace);

    private static void Punches(TableRows rows, AnalyticsDayData data)
    {
        var holders = ChipHolders(data);
        foreach (var r in data.Snapshot.FinishReadouts.OrderBy(r => r.Order))
        {
            var holder = holders.GetValueOrDefault(r.ChipNumber.Trim());
            var seq = 0;
            foreach (var p in CourseCodes.DecodePunchTimes(r.PunchTimes))
            {
                seq++;
                rows.Add(("day", data.Day.Number), ("readout_id", r.Id), ("participant_id", holder), ("chip", r.ChipNumber.Trim()),
                    ("seq", seq), ("code", p.ControlCode), ("clock_s", Clock(p.Time)),
                    ("clock_text", p.Time is { } t ? McpTime.Clock(t.TimeOfDay) : null));
            }
        }
    }

    private static void Readouts(TableRows rows, AnalyticsDayData data)
    {
        var holders = ChipHolders(data);
        var latest = data.Snapshot.FinishReadouts
            .Where(r => r.ChipNumber.Trim().Length > 0)
            .GroupBy(r => r.ChipNumber.Trim(), TextKey.Comparer)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Order)!.Id, TextKey.Comparer);
        foreach (var r in data.Snapshot.FinishReadouts.OrderBy(r => r.Order))
        {
            var chip = r.ChipNumber.Trim();
            rows.Add(("day", data.Day.Number), ("readout_id", r.Id), ("read_order", r.Order), ("chip", chip),
                ("participant_id", holders.GetValueOrDefault(chip)), ("latest", latest.GetValueOrDefault(chip) == r.Id),
                ("start_clock_s", Clock(r.StartTime)), ("finish_clock_s", Clock(r.FinishTime)),
                ("punch_count", CourseCodes.DecodePunchTimes(r.PunchTimes).Count),
                ("manual_status", r.ManualStatus is { } ms ? McpJson.StatusText(ms) : null),
                ("read_at", r.CreatedAt.ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    private static void RelayTeams(TableRows rows, AnalyticsDayData data)
    {
        var s = data.Snapshot;
        if (s.RelayTeams.Count == 0)
            return;
        var groups = s.Groups.ToDictionary(g => g.Id, g => g.Name);
        var clubs = s.Clubs.ToDictionary(c => c.Id, c => c.Name);
        var regions = s.Regions.ToDictionary(c => c.Id, c => c.Name);
        var results = TeamResults(data);
        foreach (var t in s.RelayTeams.OrderBy(t => t.Number ?? int.MaxValue))
        {
            results.TryGetValue(t.Id, out var tr);
            rows.Add(("day", data.Day.Number), ("team_id", t.Id), ("group_name", groups.GetValueOrDefault(t.GroupId)), ("number", t.Number),
                ("name", t.Name), ("club", t.ClubId is { } c ? clubs.GetValueOrDefault(c) : null),
                ("region", t.RegionId is { } r ? regions.GetValueOrDefault(r) : null),
                ("status", tr is null ? null : McpJson.StatusText(tr.Status)), ("time_s", tr?.Time?.TotalSeconds), ("place", tr?.Place),
                ("out_of_competition", t.OutOfCompetition || tr?.OutOfCompetition == true), ("points", tr?.Points));
        }
    }

    private static void RelayLegs(TableRows rows, AnalyticsDayData data)
    {
        foreach (var tr in TeamResults(data).Values.OrderBy(t => t.Number ?? int.MaxValue))
            foreach (var leg in tr.Legs.OrderBy(l => l.Leg))
                rows.Add(("day", data.Day.Number), ("team_id", tr.TeamId), ("team_number", tr.Number), ("leg", leg.Leg),
                    ("participant_id", leg.ParticipantId), ("status", McpJson.StatusText(leg.Status)),
                    ("leg_time_s", (leg.AdjustedTime ?? leg.LegTime)?.TotalSeconds), ("cumulative_s", leg.Cumulative?.TotalSeconds),
                    ("restarted", leg.Restarted), ("course_code", leg.CourseCode));
    }

    private static Dictionary<Guid, TeamDayResult> TeamResults(AnalyticsDayData data)
    {
        var map = new Dictionary<Guid, TeamDayResult>();
        foreach (var r in data.ResultsByLink.Values)
            if (r.Team is { } t)
                map.TryAdd(t.TeamId, t);
        return map;
    }

    // Who holds each chip that day (the first link with it; relay legs may share one).
    private static Dictionary<string, Guid> ChipHolders(AnalyticsDayData data)
    {
        var map = new Dictionary<string, Guid>(TextKey.Comparer);
        foreach (var l in data.Snapshot.Links.OrderBy(l => l.Leg ?? 0))
            if (l.Chip.Trim().Length > 0)
                map.TryAdd(l.Chip.Trim(), l.ParticipantId);
        return map;
    }

    // ── Values

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static string? Date(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double? Clock(DateTimeOffset? value) => value?.TimeOfDay.TotalSeconds;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Lower(Enum value) => value.ToString().ToLowerInvariant();

    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string ControlType(ControlPointType type) => type switch
    {
        ControlPointType.Start => "start",
        ControlPointType.Finish => "finish",
        _ => "control",
    };

    private static AnalyticsType FieldType(CustomFieldKind kind) => kind switch
    {
        CustomFieldKind.Number => AnalyticsType.Real,
        CustomFieldKind.Bool => AnalyticsType.Boolean,
        _ => AnalyticsType.Text,
    };

    private static object? FieldValue(CustomField field, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return field.Kind switch
        {
            CustomFieldKind.Number => double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
            CustomFieldKind.Bool => raw.Trim() is "1" or "true" or "True" or "так" or "+",
            _ => raw.Trim(),
        };
    }

    /// <summary>Rows of one table, filled by column name (unknown names are a bug — they throw).</summary>
    private sealed class TableRows
    {
        private readonly Dictionary<string, int> _index;

        public TableRows(IReadOnlyList<AnalyticsColumn> columns)
        {
            _index = columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.Ordinal);
            Count = columns.Count;
        }

        public int Count { get; }

        public List<object?[]> Rows { get; } = [];

        public void Add(params (string Column, object? Value)[] values)
        {
            var row = new object?[Count];
            foreach (var (column, value) in values)
                row[_index.TryGetValue(column, out var i) ? i : throw new InvalidOperationException($"No column '{column}'.")] = Normalize(value);
            Rows.Add(row);
        }

        private static object? Normalize(object? value) => value switch
        {
            null => null,
            Guid g => g.ToString(),
            bool b => b ? 1L : 0L,
            int i => (long)i,
            long l => l,
            decimal d => (double)d,
            float f => (double)f,
            double d => d,
            string s => s,
            _ => value.ToString(),
        };
    }
}
