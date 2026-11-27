using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>edit_readout</c>: what the finish page's read-out modal does — give an unknown chip's read-out to a participant
/// (or to a new one), set the read-out's status by hand, correct its chip or times, move the holder to another group,
/// set their «Бонус». A chip held by someone else is taken only with <c>take_chip</c>. One undo step.
/// </summary>
public sealed class ReadoutTools(McpContext ctx) : IMcpToolHandler
{
    private static readonly HashSet<string> Fields =
        ["readout", "participant", "new_participant", "take_chip", "status", "group", "bonus", "chip", "start_time", "finish_time"];

    public IReadOnlyCollection<string> Tools { get; } = ["edit_readout"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "edit_readout" => EditAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private async Task<object?> EditAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        if (args.Has("participant") && args.Has("new_participant"))
            throw new McpToolException("Give either 'participant' or 'new_participant', not both.");
        if (!args.Names.Any(n => Fields.Contains(n) && n is not ("readout" or "take_chip")))
            throw new McpToolException("Nothing to change: give participant, new_participant, status, group, bonus, chip, start_time or finish_time.");

        FinishReadoutLog log;
        FinishReadoutEditData data;
        IReadOnlyList<ParticipantRosterRow> roster;
        IReadOnlyList<GroupDayRow> groups;
        FinishReadoutRow row;
        string? note = null;
        using (McpContext.Pin(competition, day))
        {
            log = await ctx.Editor.GetFinishReadoutLogAsync(cancellationToken, day.Id);
            (row, note) = ResolveReadout(log, args.ReqStr("readout"), day.Number);
            data = await ctx.Editor.GetFinishReadoutEditAsync(row.Id, cancellationToken, day.Id)
                ?? throw new McpToolException("That read-out is gone (deleted or the day changed). Read get_report finish_log again.");
            roster = await ctx.Editor.GetParticipantRosterAsync(cancellationToken);
            groups = await ctx.Editor.GetGroupDayRowsAsync(cancellationToken);
        }

        string NameOf(Guid? id) => id is { } g ? roster.FirstOrDefault(p => p.ParticipantId == g)?.FullName ?? g.ToString() : "—";
        var chip = data.ChipNumber.Trim();
        var rows = new List<ChangeRow>();
        var holder = data.CurrentHolderId;

        // Who gets the chip.
        Guid? target = null;
        NewParticipantData? newcomer = null;
        if (args.Str("participant") is { } who && who.Trim().Length > 0)
        {
            var person = McpContext.ResolveParticipant(roster, who, day.Id);
            if (!person.Days.Any(d => d.DayId == day.Id && d.IsMember))
                throw new McpToolException($"{person.FullName} is not on day {day.Number}. Add them to the day first (update_participants in_day=true), or use new_participant.");
            if (holder is { } h && h != person.ParticipantId && !args.Flag("take_chip"))
                throw new McpToolException($"Chip {chip} is held by {NameOf(h)} on day {day.Number}. Ask the user; repeat with take_chip=true to give it to " +
                    $"{person.FullName} ({NameOf(h)} is left without a chip). Nothing was changed.");
            if (holder != person.ParticipantId)
            {
                target = person.ParticipantId;
                rows.Add(new ChangeRow(chip, "participant", NameOf(holder), person.FullName));
            }
        }
        else if (args.Object("new_participant") is { } np)
        {
            if (holder is { } h && !args.Flag("take_chip"))
                throw new McpToolException($"Chip {chip} is held by {NameOf(h)} on day {day.Number}. Ask the user; repeat with take_chip=true. Nothing was changed.");
            newcomer = await NewcomerAsync(competition, day, np, groups, cancellationToken);
            rows.Add(new ChangeRow(chip, "participant", NameOf(holder), newcomer.FullName + " (new)"));
        }
        var finalHolder = target ?? holder;

        // The read-out itself.
        var status = data.HasManualStatus ? data.Status : (FinishStatus?)null;
        var readoutChanged = newcomer is not null;
        if (args.Root.TryGetProperty("status", out var statusValue))
        {
            if (!McpJson.TryParseStatus(McpValues.Text(statusValue), out var s))
                throw new McpToolException("'status' must be OK, MP, DNF, DNS, DSQ, OVT or null (computed).");
            if (s != status)
            {
                rows.Add(new ChangeRow(chip, "status", status is { } o ? McpJson.StatusText(o) : $"— ({McpJson.StatusText(row.Status)})",
                    s is { } n ? McpJson.StatusText(n) : "— (computed)"));
                status = s;
                readoutChanged = true;
            }
        }
        var newChip = chip;
        if (args.Str("chip") is { } c && c.Trim().Length > 0 && !TextKey.Same(c.Trim(), chip))
        {
            newChip = c.Trim();
            rows.Add(new ChangeRow(chip, "chip", chip, newChip));
            readoutChanged = true;
        }
        var start = data.StartTime;
        var finish = data.FinishTime;
        if (args.Root.TryGetProperty("start_time", out var startValue))
        {
            start = At(McpValues.Time(McpValues.Text(startValue), "start_time"), data.StartTime ?? data.FinishTime, day);
            rows.Add(new ChangeRow(chip, "start_time", Clock(data.StartTime), Clock(start)));
            readoutChanged = true;
        }
        if (args.Root.TryGetProperty("finish_time", out var finishValue))
        {
            finish = At(McpValues.Time(McpValues.Text(finishValue), "finish_time"), data.FinishTime ?? data.StartTime, day);
            rows.Add(new ChangeRow(chip, "finish_time", Clock(data.FinishTime), Clock(finish)));
            readoutChanged = true;
        }

        // The holder's day.
        Guid? groupId = null;
        if (args.Str("group") is { } g && g.Trim().Length > 0)
        {
            if (finalHolder is null && newcomer is null)
                throw new McpToolException($"Chip {chip} has no holder on day {day.Number}: give 'participant' (or 'new_participant' with its group).");
            if (newcomer is not null)
                throw new McpToolException("For new_participant give the group inside it.");
            var group = McpContext.ResolveGroup(groups, g);
            var current = roster.FirstOrDefault(p => p.ParticipantId == finalHolder)?.Days.FirstOrDefault(d => d.DayId == day.Id);
            if (current?.GroupId != group.GroupId)
            {
                groupId = group.GroupId;
                rows.Add(new ChangeRow(NameOf(finalHolder), "group", current?.GroupName is { Length: > 0 } gn ? gn : "—", group.Name));
            }
        }
        var setsBonus = args.Root.TryGetProperty("bonus", out var bonusValue);
        int? bonus = null;
        if (setsBonus)
        {
            if (finalHolder is null && newcomer is null)
                throw new McpToolException($"Chip {chip} has no holder on day {day.Number}: give 'participant' first.");
            // «Бонус» corrects «Бали»: it counts only where the discipline scores points (as the read-out window offers it).
            var holderGroup = groupId ?? newcomer?.GroupId
                ?? roster.FirstOrDefault(p => p.ParticipantId == finalHolder)?.Days.FirstOrDefault(d => d.DayId == day.Id)?.GroupId;
            var scores = holderGroup is { } hg ? data.BonusGroupIds.Contains(hg) : data.BonusWithoutGroup;
            if (!scores)
                throw new McpToolException("«Бонус» corrects «Бали» and counts only in groups that score points (rogaine, score by count / time); " +
                    $"{(holderGroup is { } g2 ? groups.FirstOrDefault(x => x.GroupId == g2)?.Name ?? "this group" : "a runner without a group on this day")} does not. Nothing was changed.");
            bonus = McpValues.Int(bonusValue, "bonus");
            var current = roster.FirstOrDefault(p => p.ParticipantId == finalHolder)?.Days.FirstOrDefault(d => d.DayId == day.Id)?.Bonus;
            if (current == bonus)
                setsBonus = false;
            else
                rows.Add(new ChangeRow(NameOf(finalHolder), "bonus", McpValues.Show(current), McpValues.Show(bonus)));
        }

        if (rows.Count == 0)
            return new { changed = false, readout = row.Id, chip, note = "Nothing to change: the read-out already is so." };

        var summary = ChangeSummary.Of("Mcp.Change.Readout",
                $"Read-out of chip {chip} (day {day.Number}): " + string.Join("; ", rows.Take(3).Select(r => $"{r.Field} {r.Old ?? "—"} → {r.New ?? "—"}")),
                chip, day.Number.ToString(), rows.Count.ToString())
            .WithRows(rows);
        var write = new McpWrite("edit_readout", McpToolClass.Write, competition, day, summary, Undoable: true, ParticipantId: finalHolder);
        var readoutId = row.Id;
        return await ctx.Writer.RunAsync(write, async t =>
        {
            var editor = ctx.Editor;
            if (readoutChanged)
            {
                // The modal's own save: the read-out, then the chip to its holder, the group, the bonus and the status on them.
                await editor.UpdateFinishReadoutAsync(new FinishReadoutEdit
                {
                    Id = readoutId, ChipNumber = newChip, StartTime = start, FinishTime = finish, Punches = data.Punches,
                    ManualStatus = status, ReassignToParticipantId = target, NewParticipant = newcomer, GroupId = groupId,
                    SetsBonus = setsBonus, Bonus = bonus,
                }, t, day.Id);
            }
            else
            {
                // Only who holds the chip / their day: without touching the read-out (and so without resetting a status the
                // holder was given elsewhere).
                if (target is { } to)
                    await editor.ReassignParticipantDayChipAsync(to, day.Id, chip, t);
                if (groupId is { } gid && finalHolder is { } fh)
                    await editor.SetParticipantDayGroupAsync(fh, day.Id, gid, t);
                if (setsBonus && finalHolder is { } bh)
                    await editor.SetParticipantDayBonusAsync(bh, day.Id, bonus, t);
            }
            FinishReadoutRow? after;
            using (McpContext.Pin(competition, day))
                after = (await editor.GetFinishReadoutLogAsync(t, day.Id)).Rows.FirstOrDefault(r => r.Id == readoutId);
            return new
            {
                readout = readoutId,
                chip = after?.ChipNumber ?? newChip,
                participant = after is { IsKnown: true } ? after.FullName : null,
                group = after?.GroupName is { Length: > 0 } gn ? gn : null,
                status = after is null ? null : McpJson.StatusText(after.Status),
                manual_status = after?.IsManualStatus == true ? true : (bool?)null,
                elapsed_text = after?.Elapsed is { } e ? ShownTime.Result(e, after.TimeDecimals) : null,
                place = after?.Place,
                previous_holder = target is not null && holder is not null ? NameOf(holder) : null,
                note = after is { Status: FinishStatus.Dns, ResolvedStartTime: null, IsKnown: true }
                    ? string.Join(" ", new[] { note, "DNS: neither the runner nor the read-out has a start time — give the read-out's start_time here " +
                        "or the runner's start (update_participants start_time) if they did start." }.Where(n => n is not null))
                    : note,
            };
        }, call, cancellationToken);
    }

    // A read-out by its id (get_report finish_log) or by chip: the latest read of that chip on the day.
    private static (FinishReadoutRow Row, string? Note) ResolveReadout(FinishReadoutLog log, string reference, int dayNumber)
    {
        var r = reference.Trim();
        if (Guid.TryParse(r, out var id))
            return (log.Rows.FirstOrDefault(x => x.Id == id)
                ?? throw new McpToolException($"No read-out {id} on day {dayNumber} (get_report finish_log lists them)."), null);
        var reads = log.Rows.Where(x => TextKey.Same(x.ChipNumber.Trim(), r)).OrderByDescending(x => x.Order).ToList();
        return reads.Count switch
        {
            0 => throw new McpToolException($"No read-out of chip {r} on day {dayNumber}. get_report finish_log lists the read-outs."),
            1 => (reads[0], null),
            _ => (reads[0], $"Chip {r} was read {reads.Count} times; the latest read-out was edited (give its id for another)."),
        };
    }

    private async Task<NewParticipantData> NewcomerAsync(
        EventSummary competition, EventDay day, McpArgs np, IReadOnlyList<GroupDayRow> groups, CancellationToken cancellationToken)
    {
        var name = np.ReqStr("full_name").Trim();
        var group = McpContext.ResolveGroup(groups, np.Str("group") ?? throw new McpToolException("new_participant needs 'group' (the program requires it)."));
        var create = np.Flag("create_missing");
        Guid? club, region, dussh;
        using (McpContext.Pin(competition, day))
        {
            club = await ctx.ResolveDirectoryAsync("club", np.Str("club"), create, cancellationToken);
            region = await ctx.ResolveDirectoryAsync("region", np.Str("region"), create, cancellationToken);
            dussh = await ctx.ResolveDirectoryAsync("dussh", np.Str("dussh"), create, cancellationToken);
        }
        return new NewParticipantData
        {
            FullName = name,
            GroupId = group.GroupId,
            Number = np.Str("bib")?.Trim() ?? string.Empty,
            BirthDate = McpValues.Date(np.Str("birth_date"), "birth_date"),
            ClubId = club,
            RegionId = region,
            DusshId = dussh,
            Rank = np.Str("rank")?.Trim() ?? string.Empty,
            Coach = np.Str("coach")?.Trim() ?? string.Empty,
        };
    }

    // A clock time on the date (and offset) the read-out already has, else the day's date.
    private static DateTimeOffset? At(TimeSpan? time, DateTimeOffset? anchor, EventDay day)
    {
        if (time is not { } t)
            return null;
        if (anchor is { } a)
            return new DateTimeOffset(a.Date, a.Offset) + t;
        var date = (day.Date ?? DateTimeOffset.Now).Date;
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)) + t;
    }

    private static string Clock(DateTimeOffset? value) => value is { } v ? McpTime.Clock(v.TimeOfDay) : "—";
}
