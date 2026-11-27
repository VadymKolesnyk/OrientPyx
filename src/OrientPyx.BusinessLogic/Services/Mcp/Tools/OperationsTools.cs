using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Operations;
using OrientPyx.BusinessLogic.Services.Text;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// The operations of plan §6.4 — mostly not undoable, so each one is a preview with a <c>plan_id</c> first and is applied
/// (confirmed by the user) through <c>apply_plan</c>: <c>import_file</c>, <c>draw_start_times</c>, <c>mass_start</c>,
/// <c>set_start_order</c>, <c>assign_numbers</c>, <c>assign_rental_chips</c>, <c>copy_participants</c>; and
/// <c>create_competition</c>, <c>open_competition</c>.
/// </summary>
public sealed class OperationsTools(McpContext ctx) : IMcpToolHandler
{
    private const int PreviewNames = 30;

    public IReadOnlyCollection<string> Tools { get; } =
    [
        "import_file", "draw_start_times", "mass_start", "set_start_order", "assign_numbers", "assign_rental_chips",
        "copy_participants", "create_competition", "open_competition",
    ];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "import_file" => ImportAsync(args, call, cancellationToken),
        "draw_start_times" => DrawAsync(args, call, cancellationToken),
        "mass_start" => MassStartAsync(args, call, cancellationToken),
        "set_start_order" => StartOrderAsync(args, call, cancellationToken),
        "assign_numbers" => NumbersAsync(args, call, cancellationToken),
        "assign_rental_chips" => RentalChipsAsync(args, call, cancellationToken),
        "copy_participants" => CopyAsync(args, call, cancellationToken),
        "create_competition" => CreateAsync(args, call, cancellationToken),
        "open_competition" => OpenAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    /// <summary>preview=true (default) → a plan; preview=false → straight through the writer (still confirmed: risky).</summary>
    private async Task<object?> PlanOrRunAsync(
        string tool, McpArgs args, EventSummary competition, EventDay? day,
        Func<CancellationToken, Task<McpPlanDraft>> build, McpCall call, CancellationToken cancellationToken)
    {
        if (args.Flag("preview", true))
            return await ctx.Plans.CreateAsync(ctx, tool, competition, day, build, cancellationToken);
        McpPlanDraft draft;
        using (McpContext.Pin(competition, day))
            draft = await build(cancellationToken);
        var write = new McpWrite(tool, McpToolClass.Risky, competition, day, draft.Summary, draft.Undoable);
        return await ctx.Writer.RunAsync(write, draft.Apply, call, cancellationToken);
    }

    private static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(McpJson.Serialize(value))));

    // ── import_file

    private async Task<object?> ImportAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var kind = args.ReqStr("kind").Trim().ToLowerInvariant();
        if (!McpToolCatalog.ImportKinds.Contains(kind))
            throw new McpToolException($"Unknown import '{kind}'; kinds: {string.Join(", ", McpToolCatalog.ImportKinds)}.");
        if (day.IsLocked)
            throw new McpToolException($"Day {day.Number} is closed for edits (закритий день); nothing can be imported into it.");
        var (bytes, fileName) = await McpFilePolicy.ReadImportAsync(ctx.Get<IMcpFileAccess>(), kind, args.ReqStr("path"), cancellationToken);

        Func<CancellationToken, Task<McpPlanDraft>> build = kind switch
        {
            "iof_courses" => t => CoursesDraftAsync(args, day, bytes, fileName, t),
            "rental_chips" or "readouts" => t => ReadoutDraftAsync(kind, args, day, bytes, t),
            _ => t => ParticipantsDraftAsync(kind, args, day, bytes, fileName, t),
        };
        return await PlanOrRunAsync("import_file", args, competition, day, build, call, cancellationToken);
    }

    private async Task<McpPlanDraft> CoursesDraftAsync(McpArgs args, EventDay day, byte[] bytes, string fileName, CancellationToken ct)
    {
        var xml = XmlEncodingReader.DecodeXml(bytes);
        IofCourseData data;
        try
        {
            data = ctx.Get<IIofXmlParser>().Parse(xml, day.Number);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpToolException($"The file is not an IOF course data file the program reads: {ex.Message}");
        }
        if (data.RaceUnresolved)
            throw new McpToolException($"The file has several races ({string.Join(", ", data.RaceNumbers)}) and none is day {day.Number}. Ask the user which.");
        var replace = args.Flag("replace_existing", true);
        var preview = new
        {
            file = fileName,
            day = day.Number,
            race = data.RaceNumber,
            controls = data.Controls.Count,
            courses = data.Courses.Select(c => new { name = c.Name, controls = c.ControlCodes.Count, length_m = c.Length }).ToList(),
            replace_controls = replace,
            update_groups = replace,
        };
        var summary = ChangeSummary.Of("Mcp.Change.ImportCourses",
            $"Import {data.Controls.Count} controls and {data.Courses.Count} courses into day {day.Number}" + (replace ? " (replacing the day's controls)" : ""),
            data.Controls.Count.ToString(), data.Courses.Count.ToString(), day.Number.ToString());
        return new McpPlanDraft(preview, Fingerprint(new { preview, xml = xml.Length }), summary, async t =>
        {
            var editor = ctx.Editor;
            var points = await editor.ImportControlPointsAsync(data, replace, t);
            var groups = await editor.ImportGroupsAsync(data, replace, t);
            await editor.SaveDayFileAsync(fileName, bytes, t);
            return new { controls = McpJson.ToNode(points), groups = McpJson.ToNode(groups) };
        });
    }

    private async Task<McpPlanDraft> ReadoutDraftAsync(string kind, McpArgs args, EventDay day, byte[] bytes, CancellationToken ct)
    {
        var content = CsvEncodingReader.Decode(bytes);
        var selector = ctx.Get<IReadoutParserSelector>();
        var format = args.Str("format")?.Trim().ToLowerInvariant();
        var parser = format switch
        {
            null or "" => await selector.GetCurrentAsync(ct),
            "sportident" => selector.For(ReadoutType.SportIdent),
            "sporttime" => selector.For(ReadoutType.SportTime),
            _ => throw new McpToolException("'format' must be sportident or sporttime."),
        };
        if (!parser.CanParse(content))
        {
            // Say which format the file is, so the assistant can repeat the call with it instead of guessing.
            var other = new[] { (Name: "sportident", Type: ReadoutType.SportIdent), (Name: "sporttime", Type: ReadoutType.SportTime) }
                .FirstOrDefault(f => f.Name != format && selector.For(f.Type) is var p && p != parser && p.CanParse(content));
            throw new McpToolException(format is null or ""
                ? "The file is not a read-out file of the timing system chosen in «Налаштування» («Тип відмітки»)" +
                  (other.Name is { } name ? $"; it reads as {name} — repeat with format=\"{name}\" if that is the right file." : ".")
                : $"The file is not a {format} read-out file" + (other.Name is { } n2 ? $"; it reads as {n2}." : "."));
        }
        var data = parser.Parse(content);
        var note = args.Str("note")?.Trim() ?? string.Empty;
        int known;
        using (McpContext.Pin(ctx.OpenCompetition(), day))
            known = kind == "readouts" ? await ctx.Editor.CountKnownFinishReadoutsAsync(data, day.Id, ct) : 0;
        var preview = new
        {
            kind,
            day = day.Number,
            records = data.Records.Count,
            already_logged = kind == "readouts" ? known : (int?)null,
            to_add = kind == "readouts" ? data.Records.Count(r => r.ChipNumber.Trim().Length > 0) - known : (int?)null,
            format = data.SourceFormat,
        };
        var summary = kind == "rental_chips"
            ? ChangeSummary.Of("Mcp.Change.ImportRentalChips", $"Import {data.Records.Count} rental chips", data.Records.Count.ToString())
            : ChangeSummary.Of("Mcp.Change.ImportReadouts",
                $"Import {data.Records.Count} read-outs into day {day.Number}" + (known > 0 ? $" ({known} already logged, skipped)" : ""),
                data.Records.Count.ToString(), day.Number.ToString());
        return new McpPlanDraft(preview, Fingerprint(new { preview, length = content.Length }), summary, async t =>
            kind == "rental_chips"
                ? McpJson.ToNode(await ctx.Editor.ImportRentalChipsAsync(data, note, t))
                : McpJson.ToNode(await ctx.Editor.ImportFinishReadoutsAsync(data, t, day.Id)));
    }

    private async Task<McpPlanDraft> ParticipantsDraftAsync(string kind, McpArgs args, EventDay day, byte[] bytes, string fileName, CancellationToken ct)
    {
        var editor = ctx.Editor;
        UofParticipantData data;
        object? mappingShown = null;
        Func<CancellationToken, Task>? remember = null;
        if (kind == "participants_table")
        {
            CsvParticipantData file;
            try
            {
                file = Path.GetExtension(fileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                    ? ctx.Get<ISpreadsheetParser>().Parse(bytes)
                    : ctx.Get<ICsvParser>().Parse(CsvEncodingReader.Decode(bytes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new McpToolException($"The table cannot be read: {ex.Message}");
            }
            var fields = await editor.GetImportFieldsAsync(ct);
            var hash = ImportMapping.HeaderSetHash(file.Header);
            var relay = await editor.CurrentDayHasRelayAsync(ct);
            var map = args.Object("mapping") is { } explicitMap
                ? ExplicitMap(explicitMap, file.Header, fields)
                : CsvParticipantMapper.AutoMap(file.Header, fields,
                    await editor.GetImportMappingAsync(hash, ct) is { } json ? ImportMapping.Parse(json) : null, relay);
            if (!map.ContainsKey(ImportTarget.FullName))
                throw new McpToolException(
                    $"No column holds the full name. Columns: {string.Join(", ", file.Header)}. Pass 'mapping' (header → field).");
            var days = await ctx.DaysAsync(ctx.OpenCompetition(), ct);
            data = CsvParticipantMapper.BuildData(file, map, days, relay);
            mappingShown = map.OrderBy(kv => kv.Value).ToDictionary(kv => file.Header[kv.Value], kv => kv.Key.Key);
            var remembered = map.Where(kv => kv.Value >= 0 && kv.Value < file.Header.Count)
                .ToDictionary(kv => kv.Key.Key, kv => file.Header[kv.Value]);
            remember = t => editor.SaveImportMappingAsync(hash, ImportMapping.Serialize(remembered), t);
        }
        else
        {
            try
            {
                data = ctx.Get<IUofXmlParser>().Parse(XmlEncodingReader.DecodeXml(bytes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new McpToolException($"The file is not a participant list the program reads (UOF / IOF entry list): {ex.Message}");
            }
            var fileFields = await editor.GetImportFileFieldsAsync(data, ct);
            data = data with { FileFieldKeys = fileFields.Select(f => f.Key).ToList() };
        }
        if (data.Participants.Count == 0)
            throw new McpToolException("The file has no participants.");

        var scope = (args.Str("scope") ?? "all_days").Trim().ToLowerInvariant() switch
        {
            "current_day" => ParticipantImportScope.CurrentDay(day.Number,
                (args.Str("link_field") ?? "fsou_code").Trim().ToLowerInvariant() == "full_name" ? ParticipantLinkField.FullName : ParticipantLinkField.FsouCode),
            "all_days" => ParticipantImportScope.AllDays,
            var other => throw new McpToolException($"'scope' must be all_days or current_day (got '{other}')."),
        };
        var clearFirst = args.Flag("clear_first");
        var duplicates = await editor.FindImportDuplicatesAsync(data, clearFirst, scope, ct);
        var merges = args.Flag("merge_duplicates", true)
            ? duplicates
                .Select(d => (d.RowIndex, Same: d.Candidates.Where(c => c.LooksLikeSamePerson(d.Imported)).ToList()))
                .Where(x => x.Same.Count == 1)
                .ToDictionary(x => x.RowIndex, x => x.Same[0].ParticipantId)
            : [];
        if (merges.Count > 0)
            scope = scope.WithMergeInto(merges);
        var preview = await editor.PreviewParticipantImportAsync(data, scope, ct);
        // The program's preview covers the all-days import only; a day-scoped one is previewed here with the store's
        // own matching (link field, merges, ImportNameMatch), so the numbers shown are the numbers applied.
        var dayScoped = scope.Mode == ParticipantImportMode.CurrentDayOnly ? await DayScopedPreviewAsync(data, scope, day, ct) : null;
        var addedCount = dayScoped?.Added.Count ?? preview.Added.Count;
        var updatedCount = dayScoped?.Linked.Count ?? preview.Updated.Count;

        var shown = new
        {
            file = fileName,
            rows = data.Participants.Count,
            scope = scope.Mode == ParticipantImportMode.CurrentDayOnly ? $"day {day.Number} only" : "all days per the file",
            clear_first = clearFirst ? true : (bool?)null,
            mapping = mappingShown,
            added = addedCount,
            added_examples = dayScoped is not null
                ? dayScoped.Added.Take(PreviewNames).ToList()
                : preview.Added.Take(PreviewNames).Select(a => $"{a.FullName} ({a.Group})").ToList(),
            updated = updatedCount,
            updated_examples = dayScoped is not null
                ? (object)dayScoped.Linked.Take(PreviewNames).ToList()
                : preview.Updated.Take(PreviewNames)
                    .Select(u => new { u.FullName, changes = u.Changes.Select(c => McpJson.ToNode(c)).ToList() }).ToList(),
            ambiguous_names = dayScoped is { Guesses.Count: > 0 } ? dayScoped.Guesses : null,
            missing_from_file = preview.Missing.Count,
            missing_note = preview.Missing.Count > 0 ? "People missing from the file are NOT deleted by the import." : null,
            possible_duplicates = duplicates.Count,
            merged_into_existing = merges.Count,
            duplicates_examples = duplicates.Where(d => !merges.ContainsKey(d.RowIndex)).Take(10)
                .Select(d => new { row = d.Imported.FullName, candidates = d.Candidates.Select(c => $"{c.FullName} ({c.BirthYear}, {c.Club})").ToList() })
                .ToList(),
        };
        var summary = ChangeSummary.Of("Mcp.Change.ImportParticipants",
                $"Import {data.Participants.Count} participants: {addedCount} new, {updatedCount} updated" + (clearFirst ? ", deleting everyone first" : ""),
                data.Participants.Count.ToString(), addedCount.ToString(), updatedCount.ToString())
            .WithRows(dayScoped is not null
                ? dayScoped.Added.Take(ChangeSummary.MaxRows).Select(a => new ChangeRow(a, "add", null, $"day {day.Number}"))
                    .Concat(dayScoped.Linked.Take(ChangeSummary.MaxRows).Select(l => new ChangeRow(l, "update", null, $"day {day.Number}")))
                : preview.Added.Take(ChangeSummary.MaxRows).Select(a => new ChangeRow(a.FullName, "add", null, a.Group))
                    .Concat(preview.Updated.Take(ChangeSummary.MaxRows).Select(u => new ChangeRow(u.FullName, "update", null, $"{u.Changes.Count} field(s)"))));
        var finalScope = scope;
        return new McpPlanDraft(shown, Fingerprint(shown), summary, async t =>
        {
            var result = await editor.ImportParticipantsAsync(data, clearFirst, finalScope, null, t);
            if (remember is not null)
                await remember(t);
            return McpJson.ToNode(result);
        });
    }

    private sealed record DayScopedPreview(List<string> Added, List<string> Linked, List<string> Guesses);

    // What a day-scoped import does row by row, mirroring EventStore.ImportParticipants: a merge the duplicate check chose,
    // else the link field (ПІБ → ImportNameMatch among namesakes, ФСОУ code → the first holder), else a new athlete — and a
    // later row of the same new name updates that one.
    private async Task<DayScopedPreview> DayScopedPreviewAsync(UofParticipantData data, ParticipantImportScope scope, EventDay day, CancellationToken ct)
    {
        var editor = ctx.Editor;
        var roster = await editor.GetParticipantRosterAsync(ct);
        var clubs = await editor.GetClubsAsync(ct);
        var byId = roster.ToDictionary(p => p.ParticipantId);
        var byName = roster.Where(p => ParticipantNameKey.Of(p.FullName).Length > 0)
            .GroupBy(p => ParticipantNameKey.Of(p.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var byCode = roster.Where(p => !string.IsNullOrWhiteSpace(p.FsouCode))
            .GroupBy(p => p.FsouCode.Trim(), TextKey.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), TextKey.Comparer);
        var newNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName_ = scope.LinkField == ParticipantLinkField.FullName;
        var result = new DayScopedPreview([], [], []);

        for (var i = 0; i < data.Participants.Count; i++)
        {
            var src = data.Participants[i];
            var nameKey = ParticipantNameKey.Of(src.FullName);
            Guid? clubId = clubs.FirstOrDefault(c => TextKey.Same(c.Name, src.Club.Trim()))?.Id;
            ParticipantRosterRow? match = null;
            if (scope.MergeInto.TryGetValue(i, out var mergeId))
                match = byId.GetValueOrDefault(mergeId);
            else if (byName_ && nameKey.Length > 0 && byName.TryGetValue(nameKey, out var namesakes))
            {
                match = ImportNameMatch.Pick(namesakes, src.BirthDate, clubId, p => p.BirthDate, p => p.ClubId);
                if (match is not null && ImportNameMatch.IsGuess(namesakes, src.BirthDate, clubId, p => p.BirthDate, p => p.ClubId))
                    result.Guesses.Add($"row {i + 1} «{src.FullName.Trim()}»: {namesakes.Count} people have this name and nothing in the row " +
                        $"tells them apart; it goes to {match.FullName} (born {match.BirthDate?.Year.ToString() ?? "?"}, {Blank(match.ClubName) ?? "no club"}). " +
                        "Give the row a birth date or a club, or merge by hand.");
            }
            else if (!byName_ && src.FsouCode.Trim() is { Length: > 0 } code)
                match = byCode.GetValueOrDefault(code);

            if (match is null)
            {
                if (byName_ && nameKey.Length > 0 && !newNames.Add(nameKey))
                    result.Linked.Add($"{src.FullName.Trim()} — a second row of the same new athlete");
                else
                    result.Added.Add($"{src.FullName.Trim()} ({Blank(src.GroupOn(day.Number)) ?? Blank(src.Group) ?? "no group"})");
                continue;
            }
            var onDay = match.Days.Any(d => d.DayId == day.Id && d.IsMember);
            result.Linked.Add($"{src.FullName.Trim()} → {match.FullName} (born {match.BirthDate?.Year.ToString() ?? "?"}, " +
                $"{Blank(match.ClubName) ?? "no club"}): {(onDay ? $"already on day {day.Number}, updated" : $"joins day {day.Number}")}");
        }
        return result;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static readonly Dictionary<string, ImportTarget> TargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["full_name"] = ImportTarget.FullName, ["name"] = ImportTarget.FullName, ["bib"] = ImportTarget.Number,
        ["number"] = ImportTarget.Number, ["birth_date"] = ImportTarget.BirthDate, ["birth_year"] = ImportTarget.BirthDate,
        ["group"] = ImportTarget.Group, ["team"] = ImportTarget.Team, ["region"] = ImportTarget.Region, ["club"] = ImportTarget.Club,
        ["dussh"] = ImportTarget.Dussh, ["chip"] = ImportTarget.Chip, ["rank"] = ImportTarget.Rank,
        ["representative"] = ImportTarget.Representative, ["fsou_code"] = ImportTarget.FsouCode,
        ["fsou_member"] = ImportTarget.IsFsouMember, ["payment"] = ImportTarget.Payment, ["coach"] = ImportTarget.Coach,
        ["relay.team_number"] = ImportTarget.RelayTeamNumber, ["relay.team_name"] = ImportTarget.RelayTeamName,
        ["relay.leg"] = ImportTarget.RelayLeg,
    };

    private static Dictionary<ImportTarget, int> ExplicitMap(McpArgs mapping, IReadOnlyList<string> header, IReadOnlyList<ImportField> fields)
    {
        var map = new Dictionary<ImportTarget, int>();
        foreach (var column in mapping.Names)
        {
            var index = Enumerable.Range(0, header.Count).FirstOrDefault(i => TextKey.Same(header[i], column), -1);
            if (index < 0)
                throw new McpToolException($"No column '{column}' in the file; columns: {string.Join(", ", header)}.");
            var target = (mapping.Str(column) ?? string.Empty).Trim();
            if (target.Length == 0 || target.Equals("skip", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TargetNames.TryGetValue(target, out var builtIn))
                map[builtIn] = index;
            else if (target.StartsWith("field.", StringComparison.OrdinalIgnoreCase)
                     && fields.FirstOrDefault(f => string.Equals(f.Key, target[6..], StringComparison.OrdinalIgnoreCase) || f.Matches(target[6..])) is { } field)
                map[ImportTarget.Field(field.Key)] = index;
            else
                throw new McpToolException(
                    $"Unknown field '{target}'; fields: {string.Join(", ", TargetNames.Keys)}, field.<key> ({string.Join(", ", fields.Select(f => f.Key))}).");
        }
        return map;
    }

    // ── draw_start_times

    private async Task<object?> DrawAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var method = (args.Str("method") ?? "lanes").Trim().ToLowerInvariant();
        if (method is not ("lanes" or "classic"))
            throw new McpToolException("'method' must be lanes or classic.");
        var onlyGroups = args.Strings("groups");
        var overrides = args.Object("settings");
        var seed = Random.Shared.Next();

        return await PlanOrRunAsync("draw_start_times", args, competition, day, async t =>
        {
            var editor = ctx.Editor;
            var kind = method == "lanes" ? DrawSettingsKind.Lanes : DrawSettingsKind.Classic;
            var settings = await editor.GetDrawSettingsAsync(day.Id, kind, t) ?? new DrawSettings();
            if (overrides is not null)
                settings = SettingsTools.MergePatch(McpJson.ToNode(settings), System.Text.Json.Nodes.JsonNode.Parse(overrides.Root.GetRawText()))
                    .Deserialize<DrawSettings>(new System.Text.Json.JsonSerializerOptions(McpJson.Options) { PropertyNameCaseInsensitive = true })
                    ?? settings;
            var prep = await editor.GetDrawPrepDataAsync(day.Id, t);
            var groups = prep.Groups
                .Where(g => onlyGroups.Count == 0 || onlyGroups.Any(n => TextKey.Same(n.Trim(), g.Name.Trim())))
                .ToList();
            if (groups.Count == 0)
                throw new McpToolException("No group to draw (the named groups have no members on this day).");

            IReadOnlyList<DrawStartAssignment> assignments;
            object arrangement;
            var draw = ctx.Get<IStartDrawService>();
            if (method == "lanes")
            {
                var lanes = settings.Lanes.Count > 0
                    ? DrawLanes.Restore(groups, settings.Lanes)
                    : DrawLanes.Distribute(groups, Math.Clamp(settings.AutoGroupCount, 1, 50));
                var start = Clock(settings.GlobalStart, "global_start");
                var interval = Clock(settings.Interval, "interval");
                var gap = (int)Math.Round(Clock(settings.GroupGap, "group_gap").TotalSeconds / Math.Max(1, interval.TotalSeconds));
                assignments = draw.Draw(lanes.Select(l => (IReadOnlyList<DrawGroup>)l).ToList(), start, interval, settings.Separation, gap, seed,
                    settings.ConsiderPreviousDays);
                arrangement = new
                {
                    global_start = settings.GlobalStart, interval = settings.Interval, separation = settings.Separation.ToString().ToLowerInvariant(),
                    lanes = lanes.Select(l => l.Select(g => g.Name).ToList()).ToList(),
                };
            }
            else
            {
                var rows = settings.Rows.Where(r => r.Selected).ToDictionary(r => r.GroupId);
                var classic = groups.Where(g => rows.ContainsKey(g.GroupId))
                    .Select(g => new ClassicDrawGroup(g, Clock(rows[g.GroupId].Start, "start"), Clock(rows[g.GroupId].Interval, "interval")))
                    .ToList();
                if (classic.Count == 0)
                    throw new McpToolException("The classic draw needs a start and an interval per group: patch settings.rows " +
                        "([{group_id, selected: true, start: \"11:00:00\", interval: \"00:02:00\"}]) — see get_settings draw_classic.");
                assignments = draw.DrawClassic(classic, settings.Separation, seed, settings.ConsiderPreviousDays);
                arrangement = classic.Select(c => new { group = c.Group.Name, start = McpTime.Clock(c.Start), interval = McpTime.Clock(c.Interval) }).ToList();
            }

            var who = groups.SelectMany(g => g.Members.Select(m => (Group: g.Name, Member: m))).ToDictionary(x => x.Member.LinkId);
            var list = assignments.Where(a => who.ContainsKey(a.LinkId)).OrderBy(a => a.StartTime)
                .Select(a => new { start = McpTime.Clock(a.StartTime), group = who[a.LinkId].Group, bib = who[a.LinkId].Member.Number, name = who[a.LinkId].Member.FullName })
                .ToList();
            var preview = new { method, day = day.Number, arrangement, starts = list.Count, first = list.FirstOrDefault()?.start, last = list.LastOrDefault()?.start, start_list = list.Take(300).ToList() };
            var drawnGroups = list.Select(x => x.group).Distinct().Count();
            var summary = ChangeSummary.Of("Mcp.Change.Draw", $"Draw start times of {list.Count} runners in {drawnGroups} group(s) (day {day.Number})",
                    list.Count.ToString(), drawnGroups.ToString(), day.Number.ToString())
                .WithRows(list.Select(x => new ChangeRow(x.name, "start", null, $"{x.start} ({x.group})")));
            var chosen = assignments.ToList();
            // A draw is random: the fingerprint is the fixed inputs, so a rebuilt plan keeps the drawn list it showed.
            return new McpPlanDraft(preview, Fingerprint(new { method, arrangement, groups = groups.Select(g => (g.GroupId, g.Members.Count)).ToList() }), summary,
                async a => new { saved = await ctx.Editor.SaveDrawStartTimesAsync(chosen, a) });
        }, call, cancellationToken);
    }

    private static TimeSpan Clock(string text, string field) =>
        McpTime.TryParse(text, out var t) ? t : throw new McpToolException($"'{field}': '{text}' is not a time (HH:mm:ss).");

    // ── mass_start

    private async Task<object?> MassStartAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var items = args.ReqObjects("groups");
        return await PlanOrRunAsync("mass_start", args, competition, day, async t =>
        {
            var data = await ctx.Editor.GetMassStartDataAsync(day.Id, t);
            var assignments = items.Select(i =>
            {
                var name = i.ReqStr("group").Trim();
                var g = data.Groups.FirstOrDefault(x => TextKey.Same(x.Name.Trim(), name))
                    ?? throw new McpToolException($"No group '{name}' on day {day.Number}; groups: {string.Join(", ", data.Groups.Select(x => x.Name))}.");
                var start = McpValues.Time(i.ReqStr("start_time"), "start_time")!.Value;
                IReadOnlyDictionary<int, TimeSpan>? restarts = null;
                if (i.Str("restart_time") is { } rt && g.IsRelay)
                    restarts = Enumerable.Range(2, Math.Max(0, g.LegCount - 1)).ToDictionary(l => l, _ => McpValues.Time(rt, "restart_time")!.Value);
                return (Group: g, Assignment: new MassStartAssignment(g.GroupId, start, restarts));
            }).ToList();
            var preview = assignments.Select(a => new
            {
                group = a.Group.Name,
                // A relay group counts its teams (as the page does): their leg-1 runners and the group's runners on no
                // team get the start.
                runners = a.Group.IsRelay ? (int?)null : a.Group.MemberCount,
                teams = a.Group.IsRelay ? a.Group.MemberCount : (int?)null,
                start = McpTime.Clock(a.Assignment.Start),
                current = a.Group.CurrentStart is { } c ? McpTime.Clock(c) : null, relay = a.Group.IsRelay ? true : (bool?)null,
                note = a.Group.IsRelay ? "leg-1 runners and runners on no team start at it" : null,
            }).ToList();
            var summary = ChangeSummary.Of("Mcp.Change.MassStart", $"Mass start of {assignments.Count} group(s) on day {day.Number}",
                    assignments.Count.ToString(), day.Number.ToString())
                .WithRows(preview.Select(p => new ChangeRow(p.group, "start", p.current, p.start)));
            var list = assignments.Select(a => a.Assignment).ToList();
            return new McpPlanDraft(preview, Fingerprint(preview), summary, async a => McpJson.ToNode(await ctx.Editor.ApplyMassStartAsync(day.Id, list, a)));
        }, call, cancellationToken);
    }

    // ── set_start_order

    private async Task<object?> StartOrderAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var groupName = args.ReqStr("group").Trim();
        var order = args.Strings("order");
        if (order.Count == 0)
            throw new McpToolException("'order' must list participants.");
        return await PlanOrRunAsync("set_start_order", args, competition, day, async t =>
        {
            var editor = ctx.Editor;
            var data = await editor.GetStartOrderDataAsync(day.Id, t);
            var group = data.Groups.FirstOrDefault(g => TextKey.Same(g.Name.Trim(), groupName))
                ?? throw new McpToolException($"No group '{groupName}' with start times on day {day.Number}.");
            var roster = await editor.GetParticipantRosterAsync(t);
            var byLink = roster.SelectMany(p => p.Days.Where(d => d.DayId == day.Id && d.LinkId is not null).Select(d => (d.LinkId!.Value, p.ParticipantId)))
                .ToDictionary(x => x.Item1, x => x.ParticipantId);
            var wanted = order.Select(r => McpContext.ResolveParticipant(roster, r, day.Id).ParticipantId).ToList();
            var members = group.Members.OrderBy(m => m.StartTime ?? TimeSpan.MaxValue).ToList();
            var listed = wanted.Select(pid => members.FirstOrDefault(m => byLink.GetValueOrDefault(m.LinkId) == pid)
                ?? throw new McpToolException($"{roster.First(p => p.ParticipantId == pid).FullName} is not in {group.Name} on day {day.Number}.")).ToList();
            var ordered = listed.Concat(members.Where(m => !listed.Contains(m))).ToList();
            var slots = members.Where(m => m.StartTime is not null).Select(m => m.StartTime!.Value).OrderBy(s => s).ToList();
            if (slots.Count == 0)
                throw new McpToolException($"{group.Name} has no start times yet on day {day.Number}: draw first.");
            var assignments = new List<DrawStartAssignment>();
            var rows = new List<ChangeRow>();
            for (var i = 0; i < ordered.Count && i < slots.Count; i++)
            {
                if (ordered[i].StartTime == slots[i])
                    continue;
                assignments.Add(new DrawStartAssignment(ordered[i].LinkId, slots[i]));
                rows.Add(new ChangeRow(ordered[i].FullName, "start", ordered[i].StartTime is { } o ? McpTime.Clock(o) : null, McpTime.Clock(slots[i])));
            }
            var preview = new { group = group.Name, day = day.Number, order = ordered.Take(slots.Count).Select((m, i) => $"{McpTime.Clock(slots[i])} {m.FullName}").ToList(), changed = assignments.Count };
            var summary = ChangeSummary.Of("Mcp.Change.StartOrder", $"Start order of {group.Name} (day {day.Number}): {assignments.Count} start(s) change",
                group.Name, day.Number.ToString(), assignments.Count.ToString()).WithRows(rows);
            return new McpPlanDraft(preview, Fingerprint(preview), summary, async a => new { saved = await ctx.Editor.SaveDrawStartTimesAsync(assignments, a) });
        }, call, cancellationToken);
    }

    // ── assign_numbers

    private async Task<object?> NumbersAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var first = args.ReqInt("first_number");
        var onlyMissing = args.Flag("only_missing", true);
        var groups = args.Strings("groups");
        return await PlanOrRunAsync("assign_numbers", args, competition, day, async t =>
        {
            var editor = ctx.Editor;
            var rows = (await editor.GetParticipantDayRowsAsync(t))
                .Where(r => groups.Count == 0 || groups.Any(g => TextKey.Same(g.Trim(), r.GroupName.Trim())))
                .Where(r => !onlyMissing || r.Number.Trim().Length == 0)
                .OrderBy(r => r.Order)
                .ToList();
            var taken = (await editor.GetParticipantRosterAsync(t))
                .Where(p => !rows.Any(r => r.ParticipantId == p.ParticipantId) && p.Number.Trim().Length > 0)
                .Select(p => p.Number.Trim()).ToHashSet(TextKey.Comparer);
            var next = first;
            var assignments = new List<(Guid ParticipantId, string Number)>();
            var changes = new List<ChangeRow>();
            foreach (var r in rows)
            {
                while (taken.Contains(next.ToString()))
                    next++;
                assignments.Add((r.ParticipantId, next.ToString()));
                changes.Add(new ChangeRow(r.FullName, "bib", r.Number, next.ToString()));
                next++;
            }
            if (assignments.Count == 0)
                throw new McpToolException("Nobody to number (everyone in the selection already has a bib).");
            var preview = new { day = day.Number, people = assignments.Count, from = assignments[0].Number, to = assignments[^1].Number, examples = changes.Take(PreviewNames).Select(c => $"{c.New} {c.Subject}").ToList() };
            var summary = ChangeSummary.Of("Mcp.Change.Numbers", $"Bibs {preview.from}–{preview.to} to {assignments.Count} participant(s)",
                preview.from, preview.to, assignments.Count.ToString()).WithRows(changes);
            return new McpPlanDraft(preview, Fingerprint(assignments), summary, async a => new { saved = await ctx.Editor.SetParticipantNumbersBatchAsync(assignments, a) });
        }, call, cancellationToken);
    }

    // ── assign_rental_chips

    private async Task<object?> RentalChipsAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var groups = args.Strings("groups");
        return await PlanOrRunAsync("assign_rental_chips", args, competition, day, async t =>
        {
            var editor = ctx.Editor;
            var rows = (await editor.GetParticipantDayRowsAsync(t)).OrderBy(r => r.Order).ToList();
            var used = rows.Where(r => r.Chip.Trim().Length > 0).Select(r => r.Chip.Trim()).ToHashSet(TextKey.Comparer);
            var free = (await editor.GetRentalChipsAsync(t)).Select(c => c.Number.Trim()).Where(n => n.Length > 0 && !used.Contains(n)).ToList();
            var needy = rows.Where(r => r.Chip.Trim().Length == 0)
                .Where(r => groups.Count == 0 || groups.Any(g => TextKey.Same(g.Trim(), r.GroupName.Trim()))).ToList();
            var assignments = needy.Zip(free, (r, chip) => (r.ParticipantId, day.Id, chip)).ToList();
            if (assignments.Count == 0)
                throw new McpToolException(needy.Count == 0 ? "Everyone in the selection already has a chip." : "There are no free rental chips.");
            var names = needy.ToDictionary(r => r.ParticipantId, r => r.FullName);
            var preview = new
            {
                day = day.Number, assigned = assignments.Count, still_without_chip = Math.Max(0, needy.Count - free.Count),
                examples = assignments.Take(PreviewNames).Select(a => $"{a.chip} → {names[a.ParticipantId]}").ToList(),
            };
            var summary = ChangeSummary.Of("Mcp.Change.RentalChips", $"Give {assignments.Count} rental chip(s) on day {day.Number}",
                    assignments.Count.ToString(), day.Number.ToString())
                .WithRows(assignments.Select(a => new ChangeRow(names[a.ParticipantId], "chip", null, a.chip)));
            var batch = assignments.Select(a => (a.ParticipantId, DayId: a.Id, Chip: a.chip)).ToList();
            return new McpPlanDraft(preview, Fingerprint(batch), summary, async a => new { saved = await ctx.Editor.SetParticipantDayChipsBatchAsync(batch, a) });
        }, call, cancellationToken);
    }

    // ── copy_participants

    private async Task<object?> CopyAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var from = await ctx.DayAsync(competition, args.ReqInt("from_day"), cancellationToken);
        var to = await ctx.DayAsync(competition, args.ReqInt("to_day"), cancellationToken);
        if (from.Id == to.Id)
            throw new McpToolException("'from_day' and 'to_day' must differ.");
        if (to.IsLocked)
            throw new McpToolException($"Day {to.Number} is closed for edits.");
        var options = new CopyParticipantsOptions(Chip: args.Flag("chips", true), StartTime: args.Flag("start_times"));
        return await PlanOrRunAsync("copy_participants", args, competition, to, async t =>
        {
            var roster = await ctx.Editor.GetParticipantRosterAsync(t);
            var source = roster.Count(p => p.Days.Any(d => d.DayId == from.Id && d.IsMember));
            var already = roster.Count(p => p.Days.Any(d => d.DayId == from.Id && d.IsMember) && p.Days.Any(d => d.DayId == to.Id && d.IsMember));
            var preview = new { from_day = from.Number, to_day = to.Number, on_source_day = source, already_on_target = already, to_copy = source - already, chips = options.Chip, start_times = options.StartTime };
            var summary = ChangeSummary.Of("Mcp.Change.CopyDays", $"Copy {source - already} participant(s) from day {from.Number} to day {to.Number}",
                (source - already).ToString(), from.Number.ToString(), to.Number.ToString());
            return new McpPlanDraft(preview, Fingerprint(preview), summary,
                async a => McpJson.ToNode(await ctx.Editor.CopyParticipantsBetweenDaysAsync(new CopyParticipantsRequest(from.Id, to.Id, options), a)));
        }, call, cancellationToken);
    }

    // ── create / open

    private async Task<object?> CreateAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var name = args.ReqStr("name").Trim();
        var identifier = args.ReqStr("identifier").Trim();
        if (!EventIdentifier.IsValid(identifier))
            throw new McpToolException($"'{identifier}' is not a valid identifier (letters, digits, '-', '_'; no spaces or path characters).");
        if (!await ctx.Catalog.IsIdentifierAvailableAsync(identifier, cancellationToken))
            throw new McpToolException($"A competition '{identifier}' already exists.");
        var days = args.Int("days") ?? 1;
        if (days is < 1 or > 30)
            throw new McpToolException("'days' must be 1–30.");
        var start = McpValues.Date(args.Str("start_date"), "start_date");
        var end = McpValues.Date(args.Str("end_date"), "end_date") ?? (start is { } s ? s.AddDays(days - 1) : null);
        var o = args.Object("officials");
        var officials = o is null ? null : new CompetitionOfficials(
            o.Str("course_setter") ?? "", o.Str("course_setter_category") ?? "", o.Str("chief_judge") ?? "",
            o.Str("chief_judge_category") ?? "", o.Str("chief_secretary") ?? "", o.Str("chief_secretary_category") ?? "", o.Str("jury") ?? "");
        var venue = args.Str("venue")?.Trim() ?? string.Empty;
        var summary = ChangeSummary.Of("Mcp.Change.CreateCompetition", $"Create competition «{name}» ({identifier}), {days} day(s)", name, identifier, days.ToString());
        var write = new McpWrite("create_competition", McpToolClass.Risky, null, null, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async t =>
        {
            var created = await ctx.Catalog.CreateEventAsync(name, identifier, venue, days, start, end, officials, t);
            return new { identifier = created.Identifier, name = created.Name, days, next = "Call open_competition to work on it." };
        }, call, cancellationToken);
    }

    private async Task<object?> OpenAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var identifier = args.ReqStr("identifier").Trim();
        var target = await ctx.Catalog.FindByIdentifierAsync(identifier, cancellationToken)
            ?? throw new McpToolException($"No competition '{identifier}' (list_competitions).");
        if (McpContext.SameCompetition(ctx.Session.CurrentEvent, target) && args.Int("day") is null)
            return new { identifier, open = true, note = "Already open." };
        var day = args.Int("day");
        var summary = ChangeSummary.Of("Mcp.Change.OpenCompetition", $"Open competition «{target.Name}» in the window" + (day is { } d ? $", day {d}" : ""),
            target.Name, day?.ToString() ?? "");
        var write = new McpWrite("open_competition", McpToolClass.Risky, null, null, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async t =>
        {
            if (!await ctx.Ui.OpenCompetitionAsync(target, day, t))
                throw new McpToolException($"OrientPyx could not open '{identifier}'.");
            return new { identifier, open = true, day = ctx.Session.CurrentDay?.Number };
        }, call, cancellationToken);
    }
}
