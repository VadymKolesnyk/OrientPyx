using System.Text.Json;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>add_participants</c>, <c>update_participants</c>, <c>remove_participants</c>. A call is planned first (every name
/// resolved, every value checked, «Деталі» rows built — nothing written), then the plan runs as one change through the
/// same per-field editor methods a table cell uses.
/// </summary>
public sealed class ParticipantTools(McpContext ctx) : IMcpToolHandler
{
    private static readonly HashSet<string> IdentityKeys =
    [
        "full_name", "bib", "number", "birth_date", "club", "region", "dussh", "rank", "coach", "representative", "team",
        "fsou_code", "fsou_member", "note", "payment", "checked_in", "pays_raised_fee",
    ];

    private static readonly HashSet<string> DayKeys =
        ["in_day", "group", "chip", "start_time", "out_of_competition", "status", "bonus", "raised_fee"];

    public IReadOnlyCollection<string> Tools { get; } = ["add_participants", "update_participants", "remove_participants"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "add_participants" => AddAsync(args, call, cancellationToken),
        "update_participants" => UpdateAsync(args, call, cancellationToken),
        "remove_participants" => RemoveAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    // ── update_participants

    private async Task<object?> UpdateAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var windowDay = await ctx.DayAsync(competition, null, cancellationToken);
        var changes = args.ReqObjects("changes");
        var planner = await Planner.CreateAsync(ctx, competition, args, cancellationToken);

        using (McpContext.Pin(competition, windowDay))
        {
            foreach (var change in changes)
            {
                var day = await ctx.DayAsync(competition, change.Int("day"), cancellationToken);
                var person = McpContext.ResolveParticipant(planner.Roster, change.ReqStr("participant"), day.Id);
                var fields = change.Object("fields") ?? throw new McpToolException("Every change needs 'fields'.");
                await planner.PlanFieldsAsync(person, day, fields, cancellationToken);
            }
        }

        var people = changes.Count;
        var summary = (planner.Deleted.Count == 0
                ? ChangeSummary.Of("Mcp.Change.UpdateParticipants", $"Change {people} participant(s): {planner.Rows.Count} value(s)",
                    people.ToString())
                : ChangeSummary.Of("Mcp.Change.UpdateParticipantsDeleting",
                    $"Change {people} participant(s): {planner.Rows.Count} value(s); {planner.Deleted.Count} of them are deleted from the competition",
                    people.ToString(), planner.Deleted.Count.ToString()))
            .WithRows(planner.Rows);
        // Taking someone off their only day deletes them (RemoveParticipantFromDayAsync): it counts as a deletion.
        var cls = McpToolPolicy.BySize(McpToolClass.Write, planner.Deleted.Count, people);
        var write = new McpWrite("update_participants", cls, competition, windowDay, summary, Undoable: true, planner.SingleParticipant);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            await planner.ExecuteAsync(token);
            return new
            {
                changed_values = planner.Rows.Count,
                participants = people,
                deleted_from_competition = planner.Deleted.Count == 0 ? null : planner.Deleted,
                warnings = planner.Warnings.Count == 0 ? null : planner.Warnings,
            };
        }, call, cancellationToken);
    }

    // ── add_participants

    private async Task<object?> AddAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var windowDay = await ctx.DayAsync(competition, args, cancellationToken);
        var allDays = await ctx.DaysAsync(competition, cancellationToken);
        var people = args.ReqObjects("participants");

        List<EventDay> days;
        if (args.Get("days") is null)
            days = [windowDay];
        else
            days = args.Strings("days").Select(s => int.TryParse(s, out var n)
                ? allDays.FirstOrDefault(d => d.Number == n) ?? throw new McpToolException($"There is no day {n}.")
                : throw new McpToolException($"'days': '{s}' is not a day number.")).ToList();

        var planner = await Planner.CreateAsync(ctx, competition, args, cancellationToken);
        var rows = new List<ChangeRow>();
        var adds = new List<(McpArgs Person, IReadOnlyList<EventDay> Days)>();
        using (McpContext.Pin(competition, windowDay))
        {
            foreach (var person in people)
            {
                var name = person.ReqStr("full_name").Trim();
                if (planner.Roster.Any(p => TextKey.Same(p.FullName, name)))
                    planner.Warnings.Add($"«{name}» already exists in the competition; a second person with this name is added.");
                // Validate every field against a stand-in row now, so nothing is half-added later.
                var probe = await planner.ProbeAsync(person, days, cancellationToken);
                rows.Add(new ChangeRow(name, "add", null, probe));
                adds.Add((person, days));
            }
        }

        var summary = ChangeSummary.Of("Mcp.Change.AddParticipants",
            $"Add {adds.Count} participant(s)" + (days.Count > 0 ? $" to day(s) {string.Join(", ", days.Select(d => d.Number))}" : " to the roster"),
            adds.Count.ToString(), string.Join(", ", days.Select(d => d.Number))).WithRows(rows);
        var cls = McpToolPolicy.BySize(McpToolClass.Write, 0, adds.Count);
        var write = new McpWrite("add_participants", cls, competition, windowDay, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            var created = new List<object>();
            foreach (var (person, personDays) in adds)
            {
                var row = await ctx.Editor.AddRosterParticipantAsync(token)
                    ?? throw new McpToolException("OrientPyx could not add a participant.");
                var exec = await Planner.CreateAsync(ctx, competition, args, token);
                var fresh = exec.Roster.First(p => p.ParticipantId == row.ParticipantId);
                var identity = new McpArgs(Without(person.Root, exec.DayFieldKeys));
                await exec.PlanFieldsAsync(fresh, personDays.FirstOrDefault() ?? windowDay, identity, token);
                foreach (var day in personDays)
                {
                    var dayFields = DayFieldsOf(person.Root, exec.DayFieldKeys);
                    dayFields["in_day"] = true;
                    await exec.PlanFieldsAsync(fresh, day, new McpArgs(JsonSerializer.SerializeToElement(dayFields)), token);
                }
                await exec.ExecuteAsync(token);
                created.Add(new { participant_id = row.ParticipantId, full_name = person.ReqStr("full_name").Trim() });
            }
            return new { added = created, warnings = planner.Warnings.Count == 0 ? null : planner.Warnings };
        }, call, cancellationToken);
    }

    private static JsonElement Without(JsonElement person, HashSet<string> keys)
    {
        var copy = new Dictionary<string, JsonElement>();
        foreach (var p in person.EnumerateObject())
            if (!keys.Contains(p.Name))
                copy[p.Name] = p.Value;
        return JsonSerializer.SerializeToElement(copy);
    }

    private static Dictionary<string, object?> DayFieldsOf(JsonElement person, HashSet<string> dayKeys)
    {
        var copy = new Dictionary<string, object?>();
        foreach (var p in person.EnumerateObject())
            if (dayKeys.Contains(p.Name))
                copy[p.Name] = p.Value;
        return copy;
    }

    // ── remove_participants

    private async Task<object?> RemoveAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var mode = args.ReqStr("mode").Trim().ToLowerInvariant();
        if (mode is not ("from_day" or "delete"))
            throw new McpToolException("'mode' must be from_day or delete.");
        var refs = args.Strings("participants");
        if (refs.Count == 0)
            throw new McpToolException("'participants' must list at least one person.");

        List<(ParticipantRosterRow Person, Guid? LinkId)> targets;
        using (McpContext.Pin(competition, day))
        {
            var roster = await ctx.Editor.GetParticipantRosterAsync(cancellationToken);
            targets = refs.Select(r => McpContext.ResolveParticipant(roster, r, day.Id)).DistinctBy(p => p.ParticipantId)
                .Select(p => (p, p.Days.FirstOrDefault(d => d.DayId == day.Id && d.IsMember)?.LinkId))
                .ToList();
        }
        if (mode == "from_day" && targets.FirstOrDefault(t => t.LinkId is null) is { Person: not null } outside)
            throw new McpToolException($"{outside.Person.FullName} does not take part on day {day.Number}.");

        // Taking someone off their only day deletes them from the competition (RemoveParticipantFromDayAsync), as the
        // day grid warns: such a removal is a deletion — counted, said in the summary and in the answer.
        var deleting = mode == "delete"
            ? targets.Select(t => t.Person.FullName).ToList()
            : targets.Where(t => OnlyDay(t.Person, day)).Select(t => t.Person.FullName).ToList();
        var rows = targets.Select(t => new ChangeRow(t.Person.FullName, mode == "delete" ? "delete" : $"day {day.Number}", "так",
            mode == "from_day" && OnlyDay(t.Person, day) ? "— (видаляється зі змагань)" : "—"));
        var summary = (mode == "delete"
                ? ChangeSummary.Of("Mcp.Change.DeleteParticipants", $"Delete {targets.Count} participant(s) from the competition", targets.Count.ToString())
                : deleting.Count == 0
                    ? ChangeSummary.Of("Mcp.Change.RemoveFromDay", $"Take {targets.Count} participant(s) off day {day.Number}", targets.Count.ToString(), day.Number.ToString())
                    : ChangeSummary.Of("Mcp.Change.RemoveFromDayDeleting",
                        $"Take {targets.Count} participant(s) off day {day.Number}; {deleting.Count} of them are deleted from the competition (no other day)",
                        targets.Count.ToString(), day.Number.ToString(), deleting.Count.ToString()))
            .WithRows(rows);
        var cls = McpToolPolicy.BySize(McpToolClass.Write, deleting.Count, 0);
        var write = new McpWrite("remove_participants", cls, competition, day, summary, Undoable: true,
            targets.Count == 1 ? targets[0].Person.ParticipantId : null);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            foreach (var (person, linkId) in targets)
            {
                if (mode == "delete")
                    await ctx.Editor.DeleteParticipantAsync(person.ParticipantId, token);
                else
                    await ctx.Editor.RemoveParticipantFromDayAsync(linkId!.Value, person.ParticipantId, token);
            }
            return new
            {
                removed = targets.Count,
                mode,
                deleted_from_competition = mode == "from_day" && deleting.Count > 0 ? deleting : null,
                note = mode == "from_day" && deleting.Count > 0
                    ? "These people ran on no other day, so taking them off the day deleted them from the competition (undo restores them)."
                    : null,
            };
        }, call, cancellationToken);
    }

    /// <summary>True when <paramref name="day"/> is the only day <paramref name="person"/> runs on — taking them off it deletes them.</summary>
    private static bool OnlyDay(ParticipantRosterRow person, EventDay day) =>
        person.Days.Any(d => d.DayId == day.Id && d.IsMember) && !person.Days.Any(d => d.DayId != day.Id && d.IsMember);

    /// <summary>
    /// Turns field maps into editor calls: identity fields are folded into one whole-row save per person (fresh row →
    /// change → save), every other field is its own setter, as in the tables.
    /// </summary>
    private sealed class Planner
    {
        private readonly McpContext _ctx;
        private readonly Dictionary<Guid, List<Func<ParticipantRosterRow, CancellationToken, Task<ParticipantRosterRow>>>> _identity = [];
        private readonly List<Func<CancellationToken, Task>> _ops = [];
        private readonly Dictionary<Guid, IReadOnlyList<GroupDayRow>> _groupsByDay = [];
        private readonly HashSet<Guid> _people = [];

        private Planner(McpContext ctx) => _ctx = ctx;

        public IReadOnlyList<ParticipantRosterRow> Roster { get; private set; } = [];
        public List<ChangeRow> Rows { get; } = [];
        public List<string> Warnings { get; } = [];

        /// <summary>The people this plan deletes from the competition by taking them off their only day.</summary>
        public List<string> Deleted { get; } = [];

        private bool PaymentPerDay { get; set; }
        private IReadOnlyList<CustomField> CustomFields { get; set; } = [];
        private IReadOnlyList<EntryFeeDiscount> Discounts { get; set; } = [];
        private string OnChipConflict { get; set; } = "fail";
        private bool CreateMissing { get; set; }

        public Guid? SingleParticipant => _people.Count == 1 ? _people.First() : null;

        /// <summary>The fields kept per day (payment too, when the competition keeps payment per day).</summary>
        public HashSet<string> DayFieldKeys => PaymentPerDay ? [.. DayKeys, "payment"] : DayKeys;

        public static async Task<Planner> CreateAsync(McpContext ctx, EventSummary competition, McpArgs args, CancellationToken ct)
        {
            var p = new Planner(ctx);
            using (McpContext.Pin(competition, null))
            {
                var editor = ctx.Editor;
                p.Roster = await editor.GetParticipantRosterAsync(ct);
                p.PaymentPerDay = (await editor.GetInfoAsync(ct))?.PaymentPerDay ?? false;
                p.CustomFields = await editor.GetCustomFieldsAsync(ct);
                p.Discounts = await editor.GetEntryFeeDiscountsAsync(ct);
            }
            p.OnChipConflict = (args.Str("on_chip_conflict") ?? "fail").Trim().ToLowerInvariant();
            if (p.OnChipConflict is not ("fail" or "reassign" or "swap"))
                throw new McpToolException("'on_chip_conflict' must be fail, reassign or swap.");
            p.CreateMissing = args.Flag("create_missing");
            return p;
        }

        /// <summary>Validates a new person's fields against an empty stand-in row; returns a one-line description.</summary>
        public async Task<string> ProbeAsync(McpArgs person, IReadOnlyList<EventDay> days, CancellationToken ct)
        {
            var standIn = new ParticipantRosterRow(Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, null,
                string.Empty, null, string.Empty, null, string.Empty, string.Empty, string.Empty, false, string.Empty, false,
                string.Empty, string.Empty, false, [], 0, []);
            var probe = new Planner(_ctx)
            {
                Roster = Roster, PaymentPerDay = PaymentPerDay, CustomFields = CustomFields, Discounts = Discounts,
                OnChipConflict = OnChipConflict, CreateMissing = CreateMissing,
            };
            await probe.PlanFieldsAsync(standIn, days.FirstOrDefault(), new McpArgs(Without(person.Root, DayFieldKeys)), ct, isNew: true);
            foreach (var day in days)
            {
                var dayFields = DayFieldsOf(person.Root, DayFieldKeys);
                dayFields["in_day"] = true;
                await probe.PlanFieldsAsync(standIn, day, new McpArgs(JsonSerializer.SerializeToElement(dayFields)), ct, isNew: true);
            }
            // A new person's birth year against the group's age window (the day fields alone don't carry the birth date).
            if (person.Str("group") is { Length: > 0 } groupName && BirthYearOf(standIn, person) is { } year)
                foreach (var day in days)
                    probe.AgeWarning(person.ReqStr("full_name").Trim(), year, await probe.GroupAsync(day, groupName, ct));
            Warnings.AddRange(probe.Warnings);
            return string.Join("; ", probe.Rows.Select(r => $"{r.Field}={r.New}"));
        }

        public async Task PlanFieldsAsync(ParticipantRosterRow person, EventDay? day, McpArgs fields, CancellationToken ct, bool isNew = false)
        {
            var pid = person.ParticipantId;
            if (!isNew)
                _people.Add(pid);
            var cell = day is null ? null : person.Days.FirstOrDefault(d => d.DayId == day.Id);
            var entries = fields.Root.EnumerateObject().ToList();
            var joinsDay = entries.Any(e => e.Name == "in_day" && McpValues.Bool(e.Value, "in_day"));
            var member = isNew ? joinsDay : cell?.IsMember == true;
            var name = string.IsNullOrEmpty(person.FullName) ? fields.Str("full_name") ?? "new" : person.FullName;

            // in_day first: the other day fields need the person on the day.
            foreach (var e in entries.OrderBy(e => e.Name == "in_day" ? 0 : 1))
            {
                var key = e.Name;
                var value = e.Value;
                if (DayKeys.Contains(key) || (key == "payment" && PaymentPerDay))
                {
                    if (day is null)
                        throw new McpToolException($"'{key}' is a day field: give 'day'.");
                    if (key == "in_day")
                    {
                        var join = McpValues.Bool(value, key);
                        if (join == member && !isNew)
                            continue;
                        if (join)
                        {
                            var groupName = fields.Str("group");
                            var joinGroup = groupName is null ? null : await GroupAsync(day, groupName, ct);
                            var groupId = joinGroup?.GroupId;
                            if (joinGroup is not null)
                                AgeWarning(name, BirthYearOf(person, fields), joinGroup);
                            if (day.IsLocked)
                                throw new McpToolException($"Day {day.Number} is closed for edits (закритий день).");
                            Rows.Add(new ChangeRow(name, $"day {day.Number}", "—", "так"));
                            _ops.Add(async t =>
                            {
                                var id = await Resolve(pid);
                                await _ctx.Editor.SetParticipantDayGroupAsync(id, day.Id, groupId, t);
                            });
                            member = true;
                        }
                        else
                        {
                            var linkId = cell?.LinkId ?? throw new McpToolException($"{name} is not on day {day.Number}.");
                            var lastDay = !person.Days.Any(d => d.DayId != day.Id && d.IsMember);
                            Rows.Add(new ChangeRow(name, $"day {day.Number}", "так", lastDay ? "— (видаляється зі змагань)" : "—"));
                            if (lastDay)
                            {
                                Deleted.Add(name);
                                Warnings.Add($"{name} runs on no other day: taking them off day {day.Number} deletes them from the competition (undo restores them).");
                            }
                            _ops.Add(t => _ctx.Editor.RemoveParticipantFromDayAsync(linkId, pid, t));
                            member = false;
                        }
                        continue;
                    }
                    if (!member)
                        throw new McpToolException($"{name} does not take part on day {day.Number}; add \"in_day\": true first.");
                    await PlanDayFieldAsync(name, pid, day, cell, key, value, isNew, joinsDay, ct);
                    continue;
                }

                if (key.StartsWith("field.", StringComparison.Ordinal))
                {
                    var fieldKey = key[6..];
                    var def = CustomFields.FirstOrDefault(f => string.Equals(f.Key, fieldKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(f.Label, fieldKey, StringComparison.OrdinalIgnoreCase))
                        ?? throw new McpToolException($"No additional field '{fieldKey}'; fields: {string.Join(", ", CustomFields.Select(f => f.Key))}.");
                    var text = McpValues.Text(value);
                    if (def.IsInvalid(def.Normalize(text)))
                        Warnings.Add($"{name}: «{text}» does not fit «{def.Label}»" +
                            (def.Kind == CustomFieldKind.Choice ? $" (options: {string.Join(", ", def.Options.Select(o => o.Value))})" : $" ({def.Kind.ToString().ToLowerInvariant()})") +
                            "; it is stored as typed and OrientPyx shows the cell in red.");
                    Rows.Add(new ChangeRow(name, def.Label, person.Fields.GetValueOrDefault(def.Key), text));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantFieldAsync(await Resolve(pid), def.Key, text, t));
                    continue;
                }

                if (key.StartsWith("discount.", StringComparison.Ordinal))
                {
                    var discountName = key[9..];
                    var discount = Discounts.FirstOrDefault(d => TextKey.Same(d.Name, discountName))
                        ?? throw new McpToolException($"No discount '{discountName}'; discounts: {string.Join(", ", Discounts.Select(d => d.Name))}.");
                    var on = McpValues.Bool(value, key);
                    Rows.Add(new ChangeRow(name, discount.Name, McpValues.Show(person.SelectedDiscountIds.Contains(discount.Id)), McpValues.Show(on)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDiscountAsync(await Resolve(pid), discount.Id, on, t));
                    continue;
                }

                if (!IdentityKeys.Contains(key))
                    throw new McpToolException(
                        $"Unknown field '{key}'. Person: {string.Join(", ", IdentityKeys)}, field.<key>, discount.<name>; day: {string.Join(", ", DayKeys)}.");
                await PlanIdentityAsync(name, person, key, value, isNew, ct);
            }
        }

        private async Task PlanIdentityAsync(string name, ParticipantRosterRow person, string key, JsonElement value, bool isNew, CancellationToken ct)
        {
            var pid = person.ParticipantId;
            var text = McpValues.Text(value)?.Trim() ?? string.Empty;
            switch (key)
            {
                case "checked_in":
                {
                    var on = McpValues.Bool(value, key);
                    Rows.Add(new ChangeRow(name, "checked_in", McpValues.Show(person.CheckedIn), McpValues.Show(on)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantsCheckedInAsync([await Resolve(pid)], on, t));
                    return;
                }
                case "pays_raised_fee":
                {
                    var on = McpValues.Bool(value, key);
                    Rows.Add(new ChangeRow(name, key, McpValues.Show(person.PaysRaisedFee), McpValues.Show(on)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantPaysRaisedFeeAsync(await Resolve(pid), on, t));
                    return;
                }
                case "full_name" when text.Length == 0:
                    throw new McpToolException("'full_name' cannot be empty.");
                case "bib" or "number":
                {
                    if (text.Length > 0 && !isNew && await _ctx.Editor.FindNumberConflictAsync(pid, text, ct) is { } conflict)
                        throw new McpToolException($"Bib {text} already belongs to {conflict.HolderName}; nothing was changed.");
                    if (text.Length > 0 && isNew && Roster.FirstOrDefault(p => TextKey.Same(p.Number.Trim(), text)) is { } holder)
                        throw new McpToolException($"Bib {text} already belongs to {holder.FullName}; nothing was changed.");
                    Identity(pid, name, "bib", person.Number, text, (r, _) => Task.FromResult(r with { Number = text }));
                    return;
                }
                case "club" or "region" or "dussh":
                {
                    if (text.Length > 0 && !await ExistsAsync(key, text, ct) && !CreateMissing)
                        throw new McpToolException($"There is no {key} '{text}'. Use the exact name (find) or pass create_missing=true.");
                    var old = key switch { "club" => person.ClubName, "region" => person.RegionName, _ => person.DusshName };
                    Identity(pid, name, key, old, text, async (r, t) =>
                    {
                        var id = await _ctx.ResolveDirectoryAsync(key, text, CreateMissing, t);
                        return key switch
                        {
                            "club" => r with { ClubId = id },
                            "region" => r with { RegionId = id },
                            _ => r with { DusshId = id },
                        };
                    });
                    return;
                }
                case "birth_date":
                {
                    var date = McpValues.Date(text, key);
                    Identity(pid, name, key, McpValues.Show(person.BirthDate), McpValues.Show(date), (r, _) => Task.FromResult(r with { BirthDate = date }));
                    return;
                }
                case "fsou_member":
                {
                    var on = McpValues.Bool(value, key);
                    Identity(pid, name, key, McpValues.Show(person.IsFsouMember), McpValues.Show(on), (r, _) => Task.FromResult(r with { IsFsouMember = on }));
                    return;
                }
            }

            (string? Old, Func<ParticipantRosterRow, ParticipantRosterRow> Apply) change = key switch
            {
                "full_name" => (person.FullName, r => r with { FullName = text }),
                "rank" => (person.Rank, r => r with { Rank = text }),
                "coach" => (person.Coach, r => r with { Coach = text }),
                "representative" => (person.Representative, r => r with { Representative = text }),
                "team" => (person.Team, r => r with { Team = text }),
                "fsou_code" => (person.FsouCode, r => r with { FsouCode = text }),
                "note" => (person.Note, r => r with { Note = text }),
                "payment" => (person.Payment, r => r with { Payment = text }),
                _ => throw new McpToolException($"Unknown field '{key}'."),
            };
            Identity(pid, name, key, change.Old, text, (r, _) => Task.FromResult(change.Apply(r)));
        }

        private void Identity(Guid pid, string name, string field, string? old, string? @new,
            Func<ParticipantRosterRow, CancellationToken, Task<ParticipantRosterRow>> apply)
        {
            Rows.Add(new ChangeRow(name, field, string.IsNullOrWhiteSpace(old) ? "—" : old, string.IsNullOrWhiteSpace(@new) ? "—" : @new));
            if (!_identity.TryGetValue(pid, out var list))
            {
                _identity[pid] = list = [];
                // The whole-row save runs once, before the setters, from the row as it is at that moment.
                _ops.Insert(0, async t =>
                {
                    var id = await Resolve(pid);
                    var fresh = (await _ctx.Editor.GetParticipantRosterAsync(t)).FirstOrDefault(p => p.ParticipantId == id)
                        ?? throw new McpToolException($"{name} no longer exists.");
                    foreach (var step in _identity[pid])
                        fresh = await step(fresh, t);
                    await _ctx.Editor.UpdateParticipantIdentityAsync(fresh, t);
                });
            }
            list.Add(apply);
        }

        private async Task PlanDayFieldAsync(string name, Guid pid, EventDay day, RosterDayCell? cell, string key, JsonElement value,
            bool isNew, bool joinsDay, CancellationToken ct)
        {
            var dayId = day.Id;
            var label = $"{key} (day {day.Number})";
            switch (key)
            {
                case "group":
                {
                    var groupName = McpValues.Text(value)?.Trim();
                    if (joinsDay)
                        return; // set with in_day
                    var group = string.IsNullOrEmpty(groupName) ? null : await GroupAsync(day, groupName, ct);
                    var groupId = group?.GroupId;
                    if (group is not null)
                        AgeWarning(name, Roster.FirstOrDefault(p => p.ParticipantId == pid)?.BirthDate?.Year, group);
                    Rows.Add(new ChangeRow(name, label, cell?.GroupName, groupName));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayGroupAsync(await Resolve(pid), dayId, groupId, t));
                    return;
                }
                case "chip":
                {
                    var chip = McpValues.Text(value)?.Trim() ?? string.Empty;
                    var holder = chip.Length == 0 || isNew ? null : await _ctx.Editor.FindChipHolderAsync(dayId, chip, pid, ct);
                    if (isNew && chip.Length > 0 && Roster.Any(p => p.Days.Any(d => d.DayId == dayId && d.IsMember && TextKey.Same(d.Chip.Trim(), chip))))
                        holder = "another participant";
                    Rows.Add(new ChangeRow(name, label, cell?.Chip, chip));
                    if (holder is null)
                    {
                        _ops.Add(async t => await _ctx.Editor.SetParticipantDayChipAsync(await Resolve(pid), dayId, chip, t));
                        return;
                    }
                    switch (OnChipConflict)
                    {
                        case "reassign":
                            Warnings.Add($"Chip {chip} is taken from {holder} (day {day.Number}).");
                            _ops.Add(async t => await _ctx.Editor.ReassignParticipantDayChipAsync(await Resolve(pid), dayId, chip, t));
                            return;
                        case "swap" when !isNew:
                            Warnings.Add($"Chips swapped with {holder} (day {day.Number}).");
                            var previous = cell?.Chip ?? string.Empty;
                            _ops.Add(async t => await _ctx.Editor.SwapParticipantDayChipsAsync(await Resolve(pid), dayId, chip, previous, t));
                            return;
                        default:
                            throw new McpToolException(
                                $"Chip {chip} is held by {holder} on day {day.Number}; nothing was changed. Use on_chip_conflict=reassign or swap.");
                    }
                }
                case "start_time":
                {
                    var time = McpValues.Time(McpValues.Text(value), key);
                    Rows.Add(new ChangeRow(name, label, McpValues.Show(cell?.StartTime), McpValues.Show(time)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayStartTimeAsync(await Resolve(pid), dayId, time, t));
                    return;
                }
                case "out_of_competition":
                {
                    var on = McpValues.Bool(value, key);
                    Rows.Add(new ChangeRow(name, label, McpValues.Show(cell?.OutOfCompetition), McpValues.Show(on)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayOutOfCompetitionAsync(await Resolve(pid), dayId, on, t));
                    return;
                }
                case "status":
                {
                    if (!McpJson.TryParseStatus(McpValues.Text(value), out var status))
                        throw new McpToolException("'status' must be OK, MP, DNF, DNS, DSQ, OVT or null (computed).");
                    Rows.Add(new ChangeRow(name, label, cell?.Result.Override is { } o ? McpJson.StatusText(o) : "—",
                        status is { } s ? McpJson.StatusText(s) : "—"));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayResultStatusAsync(await Resolve(pid), dayId, status, t));
                    return;
                }
                case "bonus":
                {
                    var bonus = McpValues.Int(value, key);
                    Rows.Add(new ChangeRow(name, label, McpValues.Show(cell?.Bonus), McpValues.Show(bonus)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayBonusAsync(await Resolve(pid), dayId, bonus, t));
                    return;
                }
                case "raised_fee":
                {
                    var on = McpValues.Bool(value, key);
                    Rows.Add(new ChangeRow(name, label, McpValues.Show(cell?.PaysRaisedFee), McpValues.Show(on)));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayRaisedFeeAsync(await Resolve(pid), dayId, on, t));
                    return;
                }
                case "payment":
                {
                    var text = McpValues.Text(value)?.Trim() ?? string.Empty;
                    Rows.Add(new ChangeRow(name, label, cell?.Payment, text));
                    _ops.Add(async t => await _ctx.Editor.SetParticipantDayPaymentAsync(await Resolve(pid), dayId, text, t));
                    return;
                }
            }
        }

        // The group's «вікові межі» (birth years), as the tables show a breach in red: said, not refused.
        private void AgeWarning(string name, int? birthYear, GroupDayRow group)
        {
            if (birthYear is not { } year)
                return;
            if ((group.MinBirthYear is { } min && year < min) || (group.MaxBirthYear is { } max && year > max))
                Warnings.Add($"{name} (born {year}) is outside the birth years of {group.Name} " +
                    $"({group.MinBirthYear?.ToString() ?? "…"}–{group.MaxBirthYear?.ToString() ?? "…"}); OrientPyx shows the birth date in red.");
        }

        // A birth year from the call (a new person) or the stored one.
        private static int? BirthYearOf(ParticipantRosterRow person, McpArgs fields)
        {
            if (fields.Str("birth_date") is { Length: > 0 } text)
            {
                try
                {
                    return McpValues.Date(text, "birth_date")?.Year;
                }
                catch (McpToolException)
                {
                    return null; // the field itself reports the bad date
                }
            }
            return person.BirthDate?.Year;
        }

        public async Task ExecuteAsync(CancellationToken ct)
        {
            foreach (var op in _ops)
                await op(ct);
        }

        // Ids are stable; the indirection keeps the planning code the same for a person added in the same call.
        private static Task<Guid> Resolve(Guid id) => Task.FromResult(id);

        private async Task<GroupDayRow> GroupAsync(EventDay day, string name, CancellationToken ct)
        {
            if (!_groupsByDay.TryGetValue(day.Id, out var groups))
                _groupsByDay[day.Id] = groups = await _ctx.Editor.GetGroupsForDayAsync(day.Id, ct);
            return McpContext.ResolveGroup(groups, name);
        }

        private async Task<bool> ExistsAsync(string kind, string name, CancellationToken ct)
        {
            var editor = _ctx.Editor;
            return kind switch
            {
                "club" => (await editor.GetClubsAsync(ct)).Any(c => TextKey.Same(c.Name, name)),
                "region" => (await editor.GetRegionsAsync(ct)).Any(c => TextKey.Same(c.Name, name)),
                _ => (await editor.GetDusshesAsync(ct)).Any(c => TextKey.Same(c.Name, name)),
            };
        }
    }
}
