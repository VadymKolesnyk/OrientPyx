using System.Text.Json;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>manage_directory</c>, <c>update_competition</c>, <c>manage_days</c>, <c>manage_relay</c>, <c>undo</c>, <c>redo</c>,
/// <c>list_changes</c>, <c>apply_plan</c>, <c>await_confirmation</c>.
/// </summary>
public sealed class ManageTools(McpContext ctx) : IMcpToolHandler
{
    private static readonly HashSet<string> InfoFields =
    [
        "name", "venue", "organisation", "start_date", "end_date", "course_setter", "course_setter_category", "chief_judge",
        "chief_judge_category", "chief_secretary", "chief_secretary_category", "jury", "raised_fee_enabled", "raised_fee_amount",
        "chip_rental_price_per_day", "roster_enabled", "payment_per_day", "default_points_rule",
    ];

    public IReadOnlyCollection<string> Tools { get; } =
        ["manage_directory", "update_competition", "manage_days", "manage_relay", "undo", "redo", "list_changes", "apply_plan", "await_confirmation"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "manage_directory" => DirectoryAsync(args, call, cancellationToken),
        "update_competition" => CompetitionAsync(args, call, cancellationToken),
        "manage_days" => DaysAsync(args, call, cancellationToken),
        "manage_relay" => RelayAsync(args, call, cancellationToken),
        "undo" => UndoAsync(args, call, cancellationToken),
        "redo" => RedoAsync(args, call, cancellationToken),
        "list_changes" => Task.FromResult(ListChanges(args)),
        "apply_plan" => ctx.Plans.ApplyAsync(ctx, ParseId(args, "plan_id"), call, cancellationToken),
        "await_confirmation" => ctx.Confirmations.AwaitAsync(ParseId(args, "confirmation_id"), call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private static Guid ParseId(McpArgs args, string name) =>
        args.Id(name) ?? throw new McpToolException($"'{name}' is required.");

    // ── manage_directory

    private async Task<object?> DirectoryAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var kind = args.ReqStr("kind").Trim().ToLowerInvariant();
        var action = args.ReqStr("action").Trim().ToLowerInvariant();
        if (action is not ("add" or "update" or "delete"))
            throw new McpToolException("'action' must be add, update or delete.");
        var items = args.ReqObjects("items");
        var ops = new List<Func<CancellationToken, Task>>();
        var rows = new List<ChangeRow>();

        using (McpContext.Pin(competition, null))
        {
            var editor = ctx.Editor;
            switch (kind)
            {
                case "club" or "region" or "dussh":
                {
                    var list = kind switch
                    {
                        "club" => (await editor.GetClubsAsync(cancellationToken)).Select(c => (c.Id, c.Name)).ToList(),
                        "region" => (await editor.GetRegionsAsync(cancellationToken)).Select(c => (c.Id, c.Name)).ToList(),
                        _ => (await editor.GetDusshesAsync(cancellationToken)).Select(c => (c.Id, c.Name)).ToList(),
                    };
                    foreach (var item in items)
                    {
                        var name = item.ReqStr("name").Trim();
                        var found = list.FirstOrDefault(x => TextKey.Same(x.Name, name));
                        switch (action)
                        {
                            case "add":
                                if (found.Name is not null)
                                    throw new McpToolException($"{kind} '{name}' already exists.");
                                rows.Add(new ChangeRow(name, kind, null, "add"));
                                ops.Add(async t => _ = kind switch
                                {
                                    "club" => (object?)await ctx.Editor.AddClubAsync(name, t),
                                    "region" => await ctx.Editor.AddRegionAsync(name, t),
                                    _ => await ctx.Editor.AddDusshAsync(name, t),
                                });
                                break;
                            case "update":
                            {
                                var id = found.Name is not null ? found.Id : throw new McpToolException($"No {kind} '{name}'.");
                                var newName = item.ReqStr("new_name").Trim();
                                if (list.Any(x => x.Id != id && TextKey.Same(x.Name, newName)))
                                    throw new McpToolException($"{kind} '{newName}' already exists.");
                                rows.Add(new ChangeRow(name, kind, name, newName));
                                ops.Add(t => kind switch
                                {
                                    "club" => ctx.Editor.UpdateClubAsync(new Club { Id = id, Name = newName }, t),
                                    "region" => ctx.Editor.UpdateRegionAsync(new Region { Id = id, Name = newName }, t),
                                    _ => ctx.Editor.UpdateDusshAsync(new Dussh { Id = id, Name = newName }, t),
                                });
                                break;
                            }
                            default:
                            {
                                var id = found.Name is not null ? found.Id : throw new McpToolException($"No {kind} '{name}'.");
                                rows.Add(new ChangeRow(name, kind, name, "delete"));
                                ops.Add(t => kind switch
                                {
                                    "club" => ctx.Editor.DeleteClubAsync(id, t),
                                    "region" => ctx.Editor.DeleteRegionAsync(id, t),
                                    _ => ctx.Editor.DeleteDusshAsync(id, t),
                                });
                                break;
                            }
                        }
                    }
                    break;
                }
                case "rental_chip":
                {
                    var chips = await editor.GetRentalChipsAsync(cancellationToken);
                    if (action == "add")
                    {
                        var adds = items.Select(i => (Number: i.ReqStr("number").Trim(), Note: i.Str("note")?.Trim() ?? string.Empty)).ToList();
                        rows.AddRange(adds.Select(a => new ChangeRow(a.Number, "rental_chip", null, "add")));
                        ops.Add(async t => await ctx.Editor.AddRentalChipsAsync(adds, t));
                        break;
                    }
                    foreach (var item in items)
                    {
                        var number = item.ReqStr("number").Trim();
                        var chip = chips.FirstOrDefault(c => TextKey.Same(c.Number.Trim(), number))
                            ?? throw new McpToolException($"No rental chip {number}.");
                        if (action == "delete")
                        {
                            rows.Add(new ChangeRow(number, "rental_chip", number, "delete"));
                            ops.Add(t => ctx.Editor.DeleteRentalChipAsync(chip.Id, t));
                            continue;
                        }
                        var updated = new RentalChip
                        {
                            Id = chip.Id, CreatedAt = chip.CreatedAt,
                            Number = item.Str("new_number")?.Trim() is { Length: > 0 } nn ? nn : chip.Number,
                            Note = item.Has("note") ? item.Str("note")!.Trim() : chip.Note,
                        };
                        rows.Add(new ChangeRow(number, "rental_chip", $"{chip.Number} {chip.Note}".Trim(), $"{updated.Number} {updated.Note}".Trim()));
                        ops.Add(t => ctx.Editor.UpdateRentalChipAsync(updated, t));
                    }
                    break;
                }
                case "discount":
                {
                    var discounts = await editor.GetEntryFeeDiscountsAsync(cancellationToken);
                    foreach (var item in items)
                    {
                        var name = item.ReqStr("name").Trim();
                        var found = discounts.FirstOrDefault(d => TextKey.Same(d.Name, name));
                        if (action == "delete")
                        {
                            var id = found?.Id ?? throw new McpToolException($"No discount '{name}'.");
                            rows.Add(new ChangeRow(name, "discount", name, "delete"));
                            ops.Add(t => ctx.Editor.DeleteEntryFeeDiscountAsync(id, t));
                            continue;
                        }
                        if (action == "add" && found is not null)
                            throw new McpToolException($"Discount '{name}' already exists.");
                        if (action == "update" && found is null)
                            throw new McpToolException($"No discount '{name}'.");
                        var newName = item.Str("new_name")?.Trim() is { Length: > 0 } n ? n : name;
                        var percent = item.Dec("percent") ?? found?.Percent ?? 0;
                        var rental = item.Bool("applies_to_chip_rental") ?? found?.AppliesToChipRental ?? false;
                        rows.Add(new ChangeRow(name, "discount", found is null ? null : $"{found.Percent}%", $"{newName} {percent}%"));
                        ops.Add(async t =>
                        {
                            var d = found is null ? await ctx.Editor.AddEntryFeeDiscountRowAsync(t) : found;
                            d.Name = newName;
                            d.Percent = percent;
                            d.AppliesToChipRental = rental;
                            await ctx.Editor.UpdateEntryFeeDiscountAsync(d, t);
                        });
                    }
                    break;
                }
                case "chip_price":
                {
                    var prices = await editor.GetChipPriceOverridesAsync(cancellationToken);
                    foreach (var item in items)
                    {
                        var note = (item.Str("note") ?? item.Str("name") ?? throw new McpToolException("Every chip_price item needs 'note'.")).Trim();
                        var found = prices.FirstOrDefault(p => TextKey.Same(p.Note, note));
                        if (action == "delete")
                        {
                            var id = found?.Id ?? throw new McpToolException($"No chip price '{note}'.");
                            rows.Add(new ChangeRow(note, "chip_price", note, "delete"));
                            ops.Add(t => ctx.Editor.DeleteChipPriceOverrideAsync(id, t));
                            continue;
                        }
                        if (action == "update" && found is null)
                            throw new McpToolException($"No chip price '{note}'.");
                        var price = item.Dec("price_per_day") ?? found?.PricePerDay ?? throw new McpToolException("'price_per_day' is required.");
                        var newNote = item.Str("new_note")?.Trim() is { Length: > 0 } n ? n : note;
                        rows.Add(new ChangeRow(note, "chip_price", found is null ? null : found.PricePerDay.ToString(), $"{newNote}: {price}"));
                        ops.Add(async t =>
                        {
                            var p = found ?? await ctx.Editor.AddChipPriceOverrideRowAsync(t);
                            p.Note = newNote;
                            p.PricePerDay = price;
                            await ctx.Editor.UpdateChipPriceOverrideAsync(p, t);
                        });
                    }
                    break;
                }
                case "custom_field":
                {
                    var fields = await editor.GetCustomFieldsAsync(cancellationToken);
                    foreach (var item in items)
                    {
                        var label = (item.Str("label") ?? item.Str("name") ?? item.Str("key") ?? throw new McpToolException("Every custom_field item needs 'label'.")).Trim();
                        var found = fields.FirstOrDefault(f => string.Equals(f.Key, label, StringComparison.OrdinalIgnoreCase) || TextKey.Same(f.Label, label));
                        if (action != "add" && found is null)
                            throw new McpToolException($"No additional field '{label}'.");
                        if (action == "add" && found is not null)
                            throw new McpToolException($"Additional field '{label}' already exists (key {found.Key}).");
                        if (found is not null && found.Source != CustomField.UserSource)
                            throw new McpToolException($"'{found.Label}' belongs to a plugin and is changed only by it.");
                        var kindText = (item.Str("kind") ?? found?.Kind.ToString() ?? "text").Trim();
                        var fieldKind = Enum.TryParse<CustomFieldKind>(kindText, true, out var k)
                            ? k
                            : throw new McpToolException("'kind' must be text, number, bool or choice.");
                        var options = item.Has("options")
                            ? item.Strings("options").Where(o => o.Trim().Length > 0).Select(o => new ChoiceOption(o.Trim())).ToList()
                            : found?.Options.ToList() ?? [];
                        if (action == "delete")
                        {
                            var key = found!.Key;
                            rows.Add(new ChangeRow(found.Label, "custom_field", found.Label, "delete"));
                            ops.Add(async t =>
                            {
                                if (await ctx.Editor.IsCustomFieldLockedAsync(key, t))
                                    throw new McpToolException($"'{label}' is in use by the program and cannot be deleted.");
                                await ctx.Editor.DeleteCustomFieldAsync(key, t);
                            });
                        }
                        else if (action == "add")
                        {
                            rows.Add(new ChangeRow(label, "custom_field", null, $"add ({fieldKind.ToString().ToLowerInvariant()})"));
                            ops.Add(async t => _ = await ctx.Editor.AddCustomFieldAsync(label, fieldKind, options, t));
                        }
                        else
                        {
                            var newLabel = item.Str("new_label")?.Trim() is { Length: > 0 } nl ? nl : found!.Label;
                            var updated = found! with { Label = newLabel, Kind = fieldKind, Options = options };
                            rows.Add(new ChangeRow(found.Label, "custom_field", $"{found.Label} {found.Kind}", $"{newLabel} {fieldKind}"));
                            ops.Add(t => ctx.Editor.UpdateCustomFieldAsync(updated, t));
                        }
                    }
                    break;
                }
                default:
                    throw new McpToolException("'kind' must be club, region, dussh, rental_chip, discount, chip_price or custom_field.");
            }
        }

        var summary = ChangeSummary.Of("Mcp.Change.Directory", $"{Capital(action)} {rows.Count} item(s) of «{kind}»", action, rows.Count.ToString(), kind)
            .WithRows(rows);
        var cls = McpToolPolicy.BySize(McpToolClass.Write, action == "delete" ? rows.Count : 0, action == "delete" ? 0 : rows.Count);
        var write = new McpWrite("manage_directory", cls, competition, null, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            foreach (var op in ops)
                await op(token);
            return new { kind, action, items = rows.Count };
        }, call, cancellationToken);
    }

    // ── update_competition

    private async Task<object?> CompetitionAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var fields = args.Object("fields") ?? throw new McpToolException("'fields' must be an object.");
        foreach (var key in fields.Names)
            if (!InfoFields.Contains(key))
                throw new McpToolException($"Unknown field '{key}'; fields: {string.Join(", ", InfoFields)}.");

        CompetitionInfo info;
        IReadOnlyList<PointsRuleChoice> rules;
        using (McpContext.Pin(competition, null))
        {
            info = await ctx.Editor.GetInfoAsync(cancellationToken) ?? throw new McpToolException("The competition has no information row.");
            rules = await ctx.Editor.GetPointsRuleChoicesAsync(cancellationToken);
        }

        var rows = new List<ChangeRow>();
        var apply = new List<Action<CompetitionInfo>>();
        bool? roster = null, perDay = null;
        foreach (var e in fields.Root.EnumerateObject())
        {
            var v = e.Value;
            string? text = McpValues.Text(v)?.Trim();
            void Set(string? old, string? @new, Action<CompetitionInfo> a)
            {
                rows.Add(new ChangeRow(competition.Name, e.Name, old, @new));
                apply.Add(a);
            }
            switch (e.Name)
            {
                case "name":
                    if (string.IsNullOrEmpty(text))
                        throw new McpToolException("'name' cannot be empty.");
                    Set(info.Name, text, i => i.Name = text);
                    break;
                case "venue": Set(info.Venue, text, i => i.Venue = text ?? string.Empty); break;
                case "organisation": Set(info.Organisation, text, i => i.Organisation = text ?? string.Empty); break;
                case "course_setter": Set(info.CourseSetter, text, i => i.CourseSetter = text ?? string.Empty); break;
                case "course_setter_category": Set(info.CourseSetterCategory, text, i => i.CourseSetterCategory = text ?? string.Empty); break;
                case "chief_judge": Set(info.ChiefJudge, text, i => i.ChiefJudge = text ?? string.Empty); break;
                case "chief_judge_category": Set(info.ChiefJudgeCategory, text, i => i.ChiefJudgeCategory = text ?? string.Empty); break;
                case "chief_secretary": Set(info.ChiefSecretary, text, i => i.ChiefSecretary = text ?? string.Empty); break;
                case "chief_secretary_category": Set(info.ChiefSecretaryCategory, text, i => i.ChiefSecretaryCategory = text ?? string.Empty); break;
                case "jury": Set(info.Jury, text, i => i.Jury = text ?? string.Empty); break;
                case "start_date":
                {
                    var d = McpValues.Date(text, e.Name);
                    Set(McpValues.Show(info.StartDate), McpValues.Show(d), i => i.StartDate = d);
                    break;
                }
                case "end_date":
                {
                    var d = McpValues.Date(text, e.Name);
                    Set(McpValues.Show(info.EndDate), McpValues.Show(d), i => i.EndDate = d);
                    break;
                }
                case "raised_fee_enabled":
                {
                    var b = McpValues.Bool(v, e.Name);
                    Set(McpValues.Show(info.RaisedFeeEnabled), McpValues.Show(b), i => i.RaisedFeeEnabled = b);
                    break;
                }
                case "raised_fee_amount":
                {
                    var d = McpValues.Dec(v, e.Name);
                    Set(McpValues.Show(info.RaisedFeeAmount), McpValues.Show(d), i => i.RaisedFeeAmount = d);
                    break;
                }
                case "chip_rental_price_per_day":
                {
                    var d = McpValues.Dec(v, e.Name);
                    Set(McpValues.Show(info.ChipRentalPricePerDay), McpValues.Show(d), i => i.ChipRentalPricePerDay = d);
                    break;
                }
                case "default_points_rule":
                {
                    Guid? id = string.IsNullOrEmpty(text) ? null
                        : (rules.FirstOrDefault(c => c.Rule.Id.ToString() == text || TextKey.Same(c.Rule.Name, text))
                            ?? throw new McpToolException($"No points rule '{text}'.")).Rule.Id;
                    Set(McpValues.Show(info.DefaultPointsRuleId), McpValues.Show(id), i => i.DefaultPointsRuleId = id);
                    break;
                }
                case "roster_enabled":
                    roster = McpValues.Bool(v, e.Name);
                    rows.Add(new ChangeRow(competition.Name, e.Name, McpValues.Show(info.RosterEnabled), McpValues.Show(roster)));
                    break;
                case "payment_per_day":
                    perDay = McpValues.Bool(v, e.Name);
                    rows.Add(new ChangeRow(competition.Name, e.Name, McpValues.Show(info.PaymentPerDay), McpValues.Show(perDay)));
                    break;
            }
        }

        var summary = ChangeSummary.Of("Mcp.Change.Competition", $"Change «Інформація»: {string.Join(", ", rows.Select(r => r.Field))}",
            string.Join(", ", rows.Select(r => r.Field))).WithRows(rows);
        var write = new McpWrite("update_competition", McpToolClass.Risky, competition, null, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            var editor = ctx.Editor;
            if (apply.Count > 0)
            {
                var fresh = await editor.GetInfoAsync(token) ?? throw new McpToolException("The competition has no information row.");
                apply.ForEach(a => a(fresh));
                await editor.SaveInfoAsync(fresh, token);
            }
            if (roster is { } r)
                await editor.SetRosterEnabledAsync(r, token);
            if (perDay is { } p)
                await editor.SetPaymentPerDayAsync(p, token);
            return new { changed = rows.Select(x => x.Field).ToList() };
        }, call, cancellationToken);
    }

    // A day dated outside «Інформація»'s dates widens them, so the protocols' and the dashboard's date range covers it.
    private async Task<(DateTimeOffset? Start, DateTimeOffset? End)?> WidenedDatesAsync(EventSummary competition, DateTimeOffset? date, CancellationToken ct)
    {
        if (date is not { } d)
            return null;
        CompetitionInfo? info;
        using (McpContext.Pin(competition, null))
            info = await ctx.Editor.GetInfoAsync(ct);
        return info is null ? null : Widen(info, d);
    }

    private static (DateTimeOffset? Start, DateTimeOffset? End)? Widen(CompetitionInfo info, DateTimeOffset d)
    {
        var start = info.StartDate is { } s && s.Date <= d.Date ? info.StartDate : d;
        var end = info.EndDate is { } e && e.Date >= d.Date ? info.EndDate : d;
        return start == info.StartDate && end == info.EndDate ? null : (start, end);
    }

    private static string DatesNote((DateTimeOffset? Start, DateTimeOffset? End)? widened) => widened is { } w
        ? $"; the competition dates become {McpValues.Show(w.Start)} – {McpValues.Show(w.End)}"
        : "";

    // Inside the write: widen «Інформація»'s dates when the day falls outside them.
    private async Task<object?> WidenDatesAsync(DateTimeOffset? date, CancellationToken ct)
    {
        if (date is not { } d || await ctx.Editor.GetInfoAsync(ct) is not { } info || Widen(info, d) is not { } w)
            return null;
        info.StartDate = w.Start;
        info.EndDate = w.End;
        await ctx.Editor.SaveInfoAsync(info, ct);
        return new { start_date = w.Start?.ToString("yyyy-MM-dd"), end_date = w.End?.ToString("yyyy-MM-dd") };
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── manage_days

    private async Task<object?> DaysAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var action = args.ReqStr("action").Trim().ToLowerInvariant();
        ChangeSummary summary;
        Func<CancellationToken, Task<object?>> body;
        EventDay? day = null;
        switch (action)
        {
            case "add":
            {
                var date = args.Has("date") ? McpValues.Date(args.Str("date"), "date") : null;
                var venue = args.Str("venue")?.Trim();
                var widened = await WidenedDatesAsync(competition, date, cancellationToken);
                summary = ChangeSummary.Of("Mcp.Change.AddDay", "Add a day" + (date is { } d ? $" ({d:yyyy-MM-dd})" : "") + DatesNote(widened),
                    McpValues.Show(date));
                body = async t =>
                {
                    var added = await ctx.Editor.AddDayAsync(t);
                    if (date is not null || venue is not null)
                    {
                        added.Date = date ?? added.Date;
                        added.Venue = venue ?? added.Venue;
                        await ctx.Editor.UpdateDayAsync(added, t);
                    }
                    var dates = await WidenDatesAsync(date, t);
                    return new { day = added.Number, date = added.Date?.ToString("yyyy-MM-dd"), competition_dates = dates };
                };
                break;
            }
            case "update":
            {
                day = await ctx.DayAsync(competition, args.Int("day") ?? throw new McpToolException("'day' is required."), cancellationToken);
                var target = day;
                var hasDate = args.Root.TryGetProperty("date", out _);
                var date = hasDate ? McpValues.Date(args.Str("date"), "date") : target.Date;
                var venue = args.Str("venue")?.Trim() ?? target.Venue;
                var widened = hasDate ? await WidenedDatesAsync(competition, date, cancellationToken) : null;
                summary = ChangeSummary.Of("Mcp.Change.UpdateDay", $"Day {target.Number}: date {McpValues.Show(date)}, venue {McpValues.Show(venue)}" + DatesNote(widened),
                        target.Number.ToString())
                    .WithRows([new ChangeRow($"day {target.Number}", "date", McpValues.Show(target.Date), McpValues.Show(date)),
                        new ChangeRow($"day {target.Number}", "venue", target.Venue, venue)]);
                body = async t =>
                {
                    var fresh = (await ctx.Editor.GetDaysAsync(t)).First(d => d.Id == target.Id);
                    fresh.Date = date;
                    fresh.Venue = venue;
                    await ctx.Editor.UpdateDayAsync(fresh, t);
                    var dates = hasDate ? await WidenDatesAsync(date, t) : null;
                    return new { day = fresh.Number, date = fresh.Date?.ToString("yyyy-MM-dd"), venue = fresh.Venue, competition_dates = dates };
                };
                break;
            }
            case "set_discipline":
            {
                day = await ctx.DayAsync(competition, args.Int("day") ?? throw new McpToolException("'day' is required."), cancellationToken);
                var target = day;
                var id = ctx.ResolveDiscipline(args.ReqStr("discipline"));
                summary = ChangeSummary.Of("Mcp.Change.DayDiscipline", $"Day {target.Number}: discipline {target.DefaultDiscipline.Value} → {id.Value}",
                    target.Number.ToString(), id.Value)
                    .WithRows([new ChangeRow($"day {target.Number}", "discipline", target.DefaultDiscipline.Value, id.Value)]);
                body = async t =>
                {
                    var updated = await ctx.Editor.SetDayDisciplineAsync(target.Id, id, t);
                    return new { day = target.Number, discipline = updated?.DefaultDiscipline.Value ?? id.Value };
                };
                break;
            }
            case "lock":
            {
                day = await ctx.DayAsync(competition, args.Int("day") ?? throw new McpToolException("'day' is required."), cancellationToken);
                var target = day;
                if (target.IsLocked)
                    return new { day = target.Number, locked = true, note = "Already closed." };
                summary = ChangeSummary.Of("Mcp.Change.LockDay", $"Close day {target.Number} for edits", target.Number.ToString());
                body = async t =>
                {
                    var updated = await ctx.Editor.SetDayLockedAsync(target.Id, true, t);
                    return new { day = target.Number, locked = updated?.IsLocked ?? true };
                };
                break;
            }
            case "unlock":
                throw new McpToolException(McpToolPolicy.NeverViaMcp["unlock_day"]);
            case "delete":
                throw new McpToolException(McpToolPolicy.NeverViaMcp["delete_day"]);
            default:
                throw new McpToolException("'action' must be add, update, set_discipline or lock.");
        }

        var write = new McpWrite("manage_days", McpToolClass.Risky, competition, day, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, body, call, cancellationToken);
    }

    // ── manage_relay

    private async Task<object?> RelayAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var action = args.ReqStr("action").Trim().ToLowerInvariant();
        RelayTeamsData data;
        IReadOnlyList<GroupDayRow> groups;
        using (McpContext.Pin(competition, day))
        {
            data = await ctx.Editor.GetRelayTeamsDataAsync(cancellationToken);
            groups = await ctx.Editor.GetGroupDayRowsAsync(cancellationToken);
        }

        GroupDayRow Group() => McpContext.ResolveGroup(groups, args.ReqStr("group"));
        RelayTeamRow Team()
        {
            var t = args.ReqStr("team").Trim();
            return (Guid.TryParse(t, out var id)
                    ? data.Teams.FirstOrDefault(x => x.Id == id)
                    : int.TryParse(t, out var n)
                        ? data.Teams.Where(x => x.Number == n)
                            .Where(x => !args.Has("group") || x.GroupId == Group().GroupId)
                            .Take(2).ToList() switch { [var only] => only, [] => null, _ => throw new McpToolException($"Several teams have number {n}; give 'group' or the team id.") }
                        : data.Teams.FirstOrDefault(x => TextKey.Same(x.Name, t)))
                ?? throw new McpToolException($"No relay team '{t}' on day {day.Number}.");
        }

        ChangeSummary summary;
        Func<CancellationToken, Task<object?>> body;
        switch (action)
        {
            case "create_teams":
            {
                var group = Group();
                var count = args.ReqInt("count");
                if (count is < 1 or > 500)
                    throw new McpToolException("'count' must be 1–500.");
                var first = args.Int("first_number") ?? (data.Teams.Max(t => t.Number) ?? 0) + 1;
                summary = ChangeSummary.Of("Mcp.Change.RelayTeams", $"Create {count} relay team(s) in {group.Name} from number {first}",
                    count.ToString(), group.Name, first.ToString());
                body = async t => new { created = await ctx.Editor.CreateRelayTeamsAsync(group.GroupId, count, first, t), group = group.Name };
                break;
            }
            case "update_team":
            {
                var team = Team();
                var clubId = args.Has("club") ? await ExistingDirectoryAsync("club", args.Str("club"), cancellationToken) : team.ClubId;
                var regionId = args.Has("region") ? await ExistingDirectoryAsync("region", args.Str("region"), cancellationToken) : team.RegionId;
                var edit = new RelayTeamEdit(team.Id, team.GroupId, args.Int("number") ?? team.Number, args.Str("name")?.Trim() ?? team.Name,
                    clubId, regionId, team.OutOfCompetition, team.StatusOverride, team.CourseSet);
                summary = ChangeSummary.Of("Mcp.Change.RelayTeam", $"Relay team {RelayTeamNumbers.Label(team.Number, team.Name)}: change",
                        RelayTeamNumbers.Label(team.Number, team.Name))
                    .WithRows([new ChangeRow(RelayTeamNumbers.Label(team.Number, team.Name), "team",
                        $"{team.Number} {team.Name}".Trim(), $"{edit.Number} {edit.Name}".Trim())]);
                body = async t => new { updated = await ctx.Editor.UpdateRelayTeamAsync(edit, t) };
                break;
            }
            case "place_runner":
            {
                ParticipantRosterRow person;
                using (McpContext.Pin(competition, day))
                    person = McpContext.ResolveParticipant(await ctx.Editor.GetParticipantRosterAsync(cancellationToken), args.ReqStr("participant"), day.Id);
                var team = Team();
                var leg = args.ReqInt("leg");
                var request = new RelayPlacementRequest(person.ParticipantId, day.Id, team.Number, leg, ReplaceOccupant: args.Flag("replace"), TeamId: team.Id);
                summary = ChangeSummary.Of("Mcp.Change.RelayPlace", $"{person.FullName} → team {RelayTeamNumbers.Label(team.Number, team.Name)}, leg {leg}",
                    person.FullName, RelayTeamNumbers.Label(team.Number, team.Name), leg.ToString());
                var teamLabel = RelayTeamNumbers.Label(team.Number, team.Name);
                var legCount = data.Groups.FirstOrDefault(g => g.GroupId == team.GroupId)?.LegCount;
                if (legCount is { } legs && (leg < 1 || leg > legs))
                    throw new McpToolException($"Team {teamLabel} runs {legs} leg(s); leg {leg} does not exist. Nothing was changed.");
                body = async t =>
                {
                    var outcome = await ctx.Editor.PlaceRelayRunnerAsync(request, t);
                    // A placement that did not happen is a refusal, not a «done» with an empty undo step.
                    switch (outcome.Status)
                    {
                        case RelayPlacementStatus.Placed or RelayPlacementStatus.Swapped or RelayPlacementStatus.Released
                            or RelayPlacementStatus.TeamChanged or RelayPlacementStatus.Unchanged:
                            return new { status = outcome.Status.ToString(), team = outcome.TeamLabel, leg = outcome.Leg, other_runner = Blank(outcome.OtherRunner) };
                        case RelayPlacementStatus.LegTaken:
                            throw new McpToolException($"Leg {leg} of team {teamLabel} is held by {Blank(outcome.OtherRunner) ?? "another runner"}; nothing was changed. " +
                                "Ask the user, then repeat with replace=true to put this runner there (the other one stays on the day without a leg).");
                        case RelayPlacementStatus.LegOutOfRange:
                            throw new McpToolException($"Leg {leg} is outside the legs of team {teamLabel}; nothing was changed.");
                        case RelayPlacementStatus.TeamFull:
                            throw new McpToolException($"Team {teamLabel} has a runner on every leg; name the leg (and replace=true). Nothing was changed.");
                        default:
                            throw new McpToolException($"{person.FullName} was not placed ({outcome.Status}); nothing was changed.");
                    }
                };
                break;
            }
            case "delete_team":
            {
                var team = Team();
                var label = RelayTeamNumbers.Label(team.Number, team.Name);
                var runners = team.Legs.Count(l => l.ParticipantId is not null) + team.Unplaced.Count;
                summary = ChangeSummary.Of("Mcp.Change.RelayDeleteTeam", $"Delete relay team {label} (its {runners} runner(s) stay on the day without a team)",
                        label, runners.ToString())
                    .WithRows([new ChangeRow(label, "team", label, "delete")]);
                body = async t =>
                {
                    await ctx.Editor.DeleteRelayTeamAsync(team.Id, t);
                    return new { deleted = label, runners_released = runners, note = "The runners stay on the day in their group, without a team." };
                };
                break;
            }
            case "release_runner":
            {
                ParticipantRosterRow person;
                using (McpContext.Pin(competition, day))
                    person = McpContext.ResolveParticipant(await ctx.Editor.GetParticipantRosterAsync(cancellationToken), args.ReqStr("participant"), day.Id);
                var relay = person.Days.FirstOrDefault(d => d.DayId == day.Id && d.IsMember)?.Relay;
                if (relay is null)
                    throw new McpToolException($"{person.FullName} is on no relay team on day {day.Number}; nothing to do.");
                var teamRow = data.Teams.FirstOrDefault(x => x.Id == relay.TeamId);
                var label = RelayTeamNumbers.Label(teamRow?.Number, teamRow?.Name ?? string.Empty);
                summary = ChangeSummary.Of("Mcp.Change.RelayRelease", $"Take {person.FullName} off relay team {label}", person.FullName, label)
                    .WithRows([new ChangeRow(person.FullName, "team", $"{label}, leg {relay.Leg?.ToString() ?? "—"}", "—")]);
                body = async t =>
                {
                    var released = await ctx.Editor.ReleaseRelayRunnerAsync(person.ParticipantId, t);
                    return released
                        ? new { released = person.FullName, team = label, note = "They stay on the day in their group, without a team." }
                        : throw new McpToolException($"{person.FullName} was not on a team; nothing was changed.");
                };
                break;
            }
            case "group_settings":
            {
                var group = Group();
                RelayGroupSettings current;
                using (McpContext.Pin(competition, day))
                    current = await ctx.Editor.GetRelayGroupSettingsAsync(group.GroupId, cancellationToken)
                        ?? throw new McpToolException($"{group.Name} has no relay settings on day {day.Number} (is the day a relay?).");
                var s = args.Object("settings") ?? throw new McpToolException("'settings' must be an object.");
                var updated = current;
                foreach (var key in s.Names)
                {
                    updated = key switch
                    {
                        "leg_count" => updated with { LegCount = s.ReqInt(key) is var n and >= 1 and <= 20 ? n : throw new McpToolException("'leg_count' must be 1–20.") },
                        "mass_start_time" => updated with { MassStartTime = McpValues.Time(s.Str(key), key) },
                        "restart_ranking" => updated with { RestartRanking = Enum.TryParse<RelayRestartRanking>((s.Str(key) ?? "").Replace("_", ""), true, out var rr) ? rr : throw new McpToolException("'restart_ranking' must be normal or after_clean.") },
                        "course_mode" => updated with { CourseMode = Enum.TryParse<RelayCourseMode>((s.Str(key) ?? "").Replace("_", ""), true, out var cm) ? cm : throw new McpToolException("'course_mode' must be group, by_bib, team_set or per_leg.") },
                        "team_course_set" => updated with { TeamCourseSet = s.Str(key)?.Trim() ?? string.Empty },
                        "restart_times" => updated with
                        {
                            RestartTimes = (s.Object(key) ?? throw new McpToolException("'restart_times' must be {\"leg\": \"HH:mm:ss\"}.")).Names
                                .ToDictionary(k => int.TryParse(k, out var l) ? l : throw new McpToolException($"'{k}' is not a leg number."),
                                    k => McpValues.Time(s.Object(key)!.Str(k), k) ?? throw new McpToolException($"Leg {k}: a time is required.")),
                        },
                        _ => throw new McpToolException($"Unknown relay setting '{key}'; settings: leg_count, mass_start_time, restart_times, restart_ranking, course_mode, team_course_set."),
                    };
                }
                summary = ChangeSummary.Of("Mcp.Change.RelaySettings", $"Relay settings of {group.Name} (day {day.Number})", group.Name, day.Number.ToString())
                    .WithRows([new ChangeRow(group.Name, "legs", current.LegCount.ToString(), updated.LegCount.ToString()),
                        new ChangeRow(group.Name, "course_mode", current.CourseMode.ToString(), updated.CourseMode.ToString())]);
                body = async t =>
                {
                    await ctx.Editor.SaveRelayGroupSettingsAsync(updated, t);
                    return new { group = group.Name, leg_count = updated.LegCount, course_mode = updated.CourseMode.ToString() };
                };
                break;
            }
            case "leg_courses":
            {
                var entries = args.ReqObjects("entries").Select(e =>
                {
                    var teamText = e.ReqStr("team").Trim();
                    var number = int.TryParse(teamText, out var n) ? n
                        : data.Teams.FirstOrDefault(x => x.Id.ToString() == teamText || TextKey.Same(x.Name, teamText))?.Number
                          ?? throw new McpToolException($"No relay team '{teamText}' (use its number).");
                    var group = args.Has("group") ? Group().GroupId : (Guid?)null;
                    var variant = (e.Str("variant") ?? e.Str("course") ?? string.Empty).Trim();
                    // The variant must be one of the team's group: an unknown code would never give the leg a course.
                    var teamGroup = data.Teams.Where(x => x.Number == number).Select(x => x.GroupId).FirstOrDefault(g => group is null || g == group);
                    var settings = data.Groups.FirstOrDefault(g => g.GroupId == (group ?? teamGroup));
                    if (variant.Length > 0 && settings is not null)
                    {
                        var codes = settings.CourseCodes ?? [];
                        if (codes.Count == 0)
                            throw new McpToolException($"{settings.GroupName} has no course variants on day {day.Number} (import the courses with " +
                                "their variations first); nothing was changed.");
                        if (!codes.Any(c => TextKey.Same(c.Trim(), variant)))
                            throw new McpToolException($"{settings.GroupName} has no variant '{variant}'; variants: {string.Join(", ", codes)}. Nothing was changed.");
                    }
                    return new LegCourseEntry(number, e.ReqInt("leg"), variant, group);
                }).ToList();
                summary = ChangeSummary.Of("Mcp.Change.LegCourses", $"Course variants of {entries.Count} relay leg(s)", entries.Count.ToString())
                    .WithRows(entries.Select(e => new ChangeRow($"{e.TeamNumber}.{e.Leg}", "variant", null, e.CourseKey)));
                body = async t => new { changed = await ctx.Editor.SetLegCoursesAsync(entries, t) };
                break;
            }
            default:
                throw new McpToolException("'action' must be create_teams, update_team, delete_team, place_runner, release_runner, group_settings or leg_courses.");
        }

        var write = new McpWrite("manage_relay", McpToolClass.Write, competition, day, summary, Undoable: true);
        return await ctx.Writer.RunAsync(write, body, call, cancellationToken);
    }

    private async Task<Guid?> ExistingDirectoryAsync(string kind, string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return await ctx.ResolveDirectoryAsync(kind, name, createMissing: false, cancellationToken);
    }

    // ── undo / redo / list_changes

    private async Task<object?> UndoAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.OpenCompetition();
        McpChange change;
        if (args.Id("step_id") is { } id)
        {
            change = ctx.Writer.Find(id, competition.Identifier)
                ?? throw new McpToolException("That step is not one of your own changes on the open competition (you can undo only your own; list_changes shows them).");
            if (change.Undone)
                throw new McpToolException("That change is already undone; nothing to do." + (change.CanRedo ? " redo puts it back." : string.Empty));
            if (!change.Undoable)
                throw new McpToolException($"That change ({change.Tool}) cannot be undone: it is not journalled for undo.");
        }
        else
            change = ctx.Writer.LastUndoable(competition.Identifier)
                ?? throw new McpToolException("There is no undoable change of yours on this competition.");

        var summary = ChangeSummary.Of("Mcp.Change.Undo", "Undo an earlier change of the assistant: " + change.Summary, change.Summary) with { Inner = change.Details };
        var write = new McpWrite("undo", McpToolClass.Write, competition, null, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async t =>
        {
            var outcome = await ctx.History.RevertAsync(change.ActiveStep, t);
            var r = outcome.Result;
            if (r.Status == UndoRevertStatus.Done)
                ctx.Writer.MarkUndone(change.Id, outcome.RevertStepId);
            else if (r.Status == UndoRevertStatus.AlreadyReverted)
                ctx.Writer.MarkUndone(change.Id, null);
            return Reverted(change, r, undo: true);
        }, call, cancellationToken);
    }

    private async Task<object?> RedoAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.OpenCompetition();
        McpChange change;
        if (args.Id("step_id") is { } id)
        {
            change = ctx.Writer.Find(id, competition.Identifier)
                ?? throw new McpToolException("That step is not one of your own changes on the open competition (list_changes shows them).");
            if (!change.Undone)
                throw new McpToolException("That change is not undone; there is nothing to redo.");
            if (change.RedoStep is null)
                throw new McpToolException("That change cannot be redone: when it was undone, everything it changed had already been put back by someone else.");
        }
        else
            change = ctx.Writer.LastUndone(competition.Identifier)
                ?? throw new McpToolException("There is no undone change of yours on this competition to redo.");

        var summary = ChangeSummary.Of("Mcp.Change.Redo", "Redo an undone change of the assistant: " + change.Summary, change.Summary) with { Inner = change.Details };
        var write = new McpWrite("redo", McpToolClass.Write, competition, null, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async t =>
        {
            var outcome = await ctx.History.RevertAsync(change.RedoStep!.Value, t);
            var r = outcome.Result;
            if (r.Status == UndoRevertStatus.Done)
                ctx.Writer.MarkRedone(change.Id, outcome.RevertStepId);
            return Reverted(change, r, undo: false);
        }, call, cancellationToken);
    }

    private static object Reverted(McpChange change, UndoRevertResult r, bool undo) => new
    {
        status = r.Status.ToString(),
        undone = undo ? r.Status == UndoRevertStatus.Done : (bool?)null,
        redone = undo ? (bool?)null : r.Status == UndoRevertStatus.Done,
        step_id = change.Id,
        change = change.Summary,
        changed_rows = r.ChangedRows,
        locked_day = r.LockedDayNumber,
        conflicts = r.Conflicts.Count == 0 ? null : r.Conflicts.Select(c => new
        {
            kind = c.Kind.ToString(), entity = c.Entity, row = c.Row, field = c.Field, expected = c.Expected, actual = c.Actual,
        }).ToList(),
        note = r.Status switch
        {
            UndoRevertStatus.Done => undo ? "Undone. redo (with this step_id) puts it back." : "Redone. undo (with this step_id) takes it back again.",
            UndoRevertStatus.AlreadyReverted => "Nothing to do: everything it changed is already as it was before it.",
            UndoRevertStatus.Conflict => (undo ? "Not undone" : "Not redone") + ": someone changed the same data since. Tell the user what conflicts.",
            UndoRevertStatus.DayLocked => (undo ? "Not undone" : "Not redone") + ": the day is closed for edits.",
            UndoRevertStatus.NotUndoable => "This change cannot be undone.",
            UndoRevertStatus.Evicted => "Too old (the journal keeps the latest steps only).",
            _ => null,
        },
    };

    private object? ListChanges(McpArgs args)
    {
        var competition = ctx.OpenCompetition();
        var limit = Math.Clamp(args.Int("limit") ?? 30, 1, 200);
        var all = ctx.Writer.Changes(competition.Identifier);
        return new
        {
            competition = competition.Identifier,
            total = all.Count,
            changes = all.AsEnumerable().Reverse().Take(limit).Select(c => new
            {
                step_id = c.Id,
                at = c.At.ToString("yyyy-MM-dd HH:mm:ss"),
                tool = c.Tool,
                day = c.Day,
                summary = c.Summary,
                details = c.Details?.Rows is { Count: > 0 } rows
                    ? rows.Take(5).Select(r => $"{r.Subject} · {r.Field}: {r.Old ?? "—"} → {r.New ?? "—"}").Append(rows.Count > 5 ? $"… {rows.Count - 5} more" : null)
                        .Where(s => s is not null).ToList()
                    : null,
                state = c.Undone ? "undone" : c.Redone ? "redone" : "done",
                can_undo = c.CanUndo,
                can_redo = c.CanRedo,
                changed_at = c.ChangedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            }).ToList(),
            note = all.Count == 0
                ? "You made no changes on this competition since OrientPyx started."
                : "Newest first; only your own changes since OrientPyx started. undo / redo take a step_id.",
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
