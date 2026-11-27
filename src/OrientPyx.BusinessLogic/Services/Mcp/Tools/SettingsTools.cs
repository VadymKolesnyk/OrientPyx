using System.Text.Json;
using System.Text.Json.Nodes;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>get_settings</c> / <c>save_settings</c>: the stored settings of the competition as JSON, and a JSON merge patch
/// over them (validated by reading the result back into the settings type before anything is saved).
/// </summary>
public sealed class SettingsTools(McpContext ctx) : IMcpToolHandler
{
    private static readonly JsonSerializerOptions ReadBack = new(McpJson.Options) { PropertyNameCaseInsensitive = true };

    public IReadOnlyCollection<string> Tools { get; } = ["get_settings", "save_settings"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "get_settings" => GetAsync(args, cancellationToken),
        "save_settings" => SaveAsync(args, call, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private async Task<object?> GetAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var kind = Kind(args);
        var competition = ctx.ReadCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        using var pin = McpContext.Pin(competition, day);
        var (value, _) = await ReadAsync(kind, args, day, cancellationToken);
        return new { kind, day = DayScoped(kind) ? day.Number : (int?)null, settings = McpJson.ToNode(value) };
    }

    private async Task<object?> SaveAsync(McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var kind = Kind(args);
        var competition = ctx.RequireCompetition(args);
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        if (args.Object("patch") is not { } patchArgs)
            throw new McpToolException("'patch' must be a JSON object (merge patch).");

        object current;
        Type type;
        using (McpContext.Pin(competition, day))
            (current, type) = await ReadAsync(kind, args, day, cancellationToken);

        var patch = JsonNode.Parse(patchArgs.Root.GetRawText()) as JsonObject ?? throw new McpToolException("'patch' must be a JSON object (merge patch).");
        // The publishing switch is the «Старт» of «Онлайн-результати», which the assistant never presses (§6.6).
        if (kind == "online_publish" && patch.ContainsKey("enabled"))
            throw new McpToolException(McpToolPolicy.NeverViaMcp["online_publishing"] +
                " ('enabled' is that switch; the other online settings — slug, title, columns… — can be patched.)");

        var merged = MergePatch(McpJson.ToNode(current), patch);
        object updated;
        try
        {
            updated = kind is "discipline" or "plugin_report"
                ? merged!
                : merged.Deserialize(type, ReadBack) ?? throw new McpToolException("The patched settings are empty.");
        }
        catch (JsonException ex)
        {
            throw new McpToolException($"The patched settings are not valid for '{kind}': {ex.Message}");
        }
        if (kind is not ("discipline" or "plugin_report"))
            CheckKeys(patch, type, string.Empty);
        if (updated is DrawSettings draw)
            CheckDrawTimes(draw);

        var summary = ChangeSummary.Of("Mcp.Change.Settings", $"Change the «{kind}» settings" + (DayScoped(kind) ? $" (day {day.Number})" : ""),
            kind, day.Number.ToString());
        var write = new McpWrite("save_settings", McpToolClass.Risky, competition, day, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async token =>
        {
            await WriteAsync(kind, args, day, updated, token);
            var (after, _) = await ReadAsync(kind, args, day, token);
            return McpJson.ToNode(after);
        }, call, cancellationToken);
    }

    // A patch may only name settings the type has — an unknown key would be dropped silently and the answer would say
    // "done" for nothing. Nested objects are checked against their own type.
    private static void CheckKeys(JsonObject patch, Type type, string path)
    {
        System.Text.Json.Serialization.Metadata.JsonTypeInfo info;
        try
        {
            info = ReadBack.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return;
        }
        if (info.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
            return;
        var properties = info.Properties.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in patch)
        {
            if (!properties.TryGetValue(key, out var property))
                throw new McpToolException($"Unknown setting '{path}{key}'; settings{(path.Length > 0 ? " of " + path.TrimEnd('.') : "")}: " +
                    $"{string.Join(", ", properties.Keys)}. Nothing was changed.");
            if (value is JsonObject nested)
                CheckKeys(nested, property.PropertyType, path + key + ".");
        }
    }

    // The draw pages read these as «год:хв:с»; a value they cannot read would break the page.
    private static void CheckDrawTimes(DrawSettings draw)
    {
        foreach (var (name, value) in new[] { ("global_start", draw.GlobalStart), ("interval", draw.Interval), ("group_gap", draw.GroupGap) })
            if (!McpTime.TryParse(value, out _) || value.Contains(' '))
                throw new McpToolException($"'{name}' must be a time HH:mm:ss (got '{value}'). Nothing was changed.");
    }

    private static string Kind(McpArgs args)
    {
        var kind = args.ReqStr("kind").Trim().ToLowerInvariant();
        return McpToolCatalog.SettingsKinds.Contains(kind)
            ? kind
            : throw new McpToolException($"Unknown settings '{kind}'; kinds: {string.Join(", ", McpToolCatalog.SettingsKinds)}.");
    }

    private static bool DayScoped(string kind) => kind is "result_protocol" or "start_protocol" or "judges_start_protocol"
        or "draw_lanes" or "draw_classic" or "discipline" or "plugin_report";

    // The stored settings (or what the page would start from when nothing is stored) and their type.
    private async Task<(object Value, Type Type)> ReadAsync(string kind, McpArgs args, EventDay day, CancellationToken ct)
    {
        var editor = ctx.Editor;
        var app = ctx.Get<IAppSettingsService>();
        switch (kind)
        {
            case "result_protocol":
                return (await editor.GetResultProtocolSettingsAsync(day.Id, ct) ?? await app.GetResultProtocolSettingsAsync(ct), typeof(ResultProtocolSettings));
            case "start_protocol":
                return (await editor.GetStartProtocolSettingsAsync(day.Id, StartProtocolKind.Regular, ct)
                    ?? await app.GetStartProtocolSettingsAsync(StartProtocolKind.Regular, ct), typeof(StartProtocolSettings));
            case "judges_start_protocol":
                return (await editor.GetStartProtocolSettingsAsync(day.Id, StartProtocolKind.Judges, ct)
                    ?? await app.GetStartProtocolSettingsAsync(StartProtocolKind.Judges, ct), typeof(StartProtocolSettings));
            case "summary_protocol":
                return (await ctx.SummarySettingsAsync(ctx.OpenCompetition(), ct), typeof(SummaryProtocolSettings));
            case "draw_lanes":
                return (await editor.GetDrawSettingsAsync(day.Id, DrawSettingsKind.Lanes, ct) ?? new DrawSettings(), typeof(DrawSettings));
            case "draw_classic":
                return (await editor.GetDrawSettingsAsync(day.Id, DrawSettingsKind.Classic, ct) ?? new DrawSettings(), typeof(DrawSettings));
            case "monitor":
                return (await editor.GetMonitorSettingsAsync(ct) ?? MonitorSettings.Empty, typeof(MonitorSettings));
            case "online_publish":
                return (await editor.GetOnlinePublishSettingsAsync(ct)
                    ?? OnlinePublishSettings.Default(ctx.OpenCompetition().Identifier, ctx.OpenCompetition().Name, string.Empty), typeof(OnlinePublishSettings));
            case "statement":
                return (await editor.GetStatementSettingsAsync(ct) ?? await app.GetStatementSettingsAsync(ct), typeof(StatementSettings));
            case "iof_export":
                return (await editor.GetIofExportSettingsAsync(ct) ?? new IofExportSettings(), typeof(IofExportSettings));
            case "chip_scan":
                return (await editor.GetChipScanSettingsAsync(ct) ?? new ChipScanSettings(), typeof(ChipScanSettings));
            case "discipline":
            {
                var id = args.Str("discipline") is { } d && d.Trim().Length > 0 ? ctx.ResolveDiscipline(d) : day.DefaultDiscipline;
                var data = await editor.GetDisciplineSettingsAsync(day.Id, id, ct)
                    ?? throw new McpToolException($"The discipline '{id.Value}' has no settings.");
                return (new SettingsValues(id.Value, Labelled(data.Definitions, ctx.Strategies.For(id).PluginId), data.Values), typeof(SettingsValues));
            }
            case "plugin_report":
            {
                var reports = PluginReportsOrEmpty();
                var report = args.Str("report")?.Trim();
                if (string.IsNullOrEmpty(report))
                    throw new McpToolException(reports.Count == 0
                        ? "'report' is required, and no switched-on plugin has a report."
                        : $"'report' is required; reports: {string.Join(", ", reports.Select(r => $"{r.Id} ({ctx.Ui.Text(r.NameKey)})"))}.");
                var data = await editor.GetPluginReportSettingsAsync(report, day.Id, ct)
                    ?? throw new McpToolException($"No plugin report '{report}' (or it has no settings)" +
                        (reports.Count == 0 ? "." : $"; reports: {string.Join(", ", reports.Select(r => r.Id))}."));
                var owner = reports.FirstOrDefault(r => r.Id == report)?.PluginId;
                return (new SettingsValues(report, Labelled(data.Definitions, owner), data.Values), typeof(SettingsValues));
            }
            default:
                throw new McpToolException($"Unknown settings '{kind}'.");
        }
    }

    private async Task WriteAsync(string kind, McpArgs args, EventDay day, object value, CancellationToken ct)
    {
        var editor = ctx.Editor;
        switch (kind)
        {
            case "result_protocol": await editor.SaveResultProtocolSettingsAsync(day.Id, (ResultProtocolSettings)value, ct); break;
            case "start_protocol": await editor.SaveStartProtocolSettingsAsync(day.Id, StartProtocolKind.Regular, (StartProtocolSettings)value, ct); break;
            case "judges_start_protocol": await editor.SaveStartProtocolSettingsAsync(day.Id, StartProtocolKind.Judges, (StartProtocolSettings)value, ct); break;
            case "summary_protocol": await editor.SaveSummaryProtocolSettingsAsync((SummaryProtocolSettings)value, ct); break;
            case "draw_lanes": await editor.SaveDrawSettingsAsync(day.Id, DrawSettingsKind.Lanes, (DrawSettings)value, ct); break;
            case "draw_classic": await editor.SaveDrawSettingsAsync(day.Id, DrawSettingsKind.Classic, (DrawSettings)value, ct); break;
            case "monitor": await editor.SaveMonitorSettingsAsync((MonitorSettings)value, ct); break;
            case "online_publish": await editor.SaveOnlinePublishSettingsAsync((OnlinePublishSettings)value, ct); break;
            case "statement": await editor.SaveStatementSettingsAsync((StatementSettings)value, ct); break;
            case "iof_export": await editor.SaveIofExportSettingsAsync((IofExportSettings)value, ct); break;
            case "chip_scan": await editor.SaveChipScanSettingsAsync((ChipScanSettings)value, ct); break;
            case "discipline":
            {
                var id = args.Str("discipline") is { } d && d.Trim().Length > 0 ? ctx.ResolveDiscipline(d) : day.DefaultDiscipline;
                var data = await editor.GetDisciplineSettingsAsync(day.Id, id, ct) ?? throw new McpToolException("No discipline settings.");
                await editor.SaveDisciplineSettingsAsync(day.Id, id, TypedValues((JsonNode)value, data.Definitions, data.Values), ct);
                break;
            }
            case "plugin_report":
            {
                var report = args.ReqStr("report").Trim();
                var data = await editor.GetPluginReportSettingsAsync(report, day.Id, ct) ?? throw new McpToolException("No report settings.");
                await editor.SavePluginReportSettingsAsync(report, day.Id, TypedValues((JsonNode)value, data.Definitions, data.Values), ct);
                break;
            }
        }
    }

    // A plugin's labels are keys of its strings («<pluginId>.<key>»); the window shows their text, so does the answer.
    private IReadOnlyList<SettingDef> Labelled(IReadOnlyList<SettingDef> definitions, string? pluginId)
    {
        string Text(string label) => ctx.Ui.Text(pluginId is null ? label : Disciplines.Plugins.PluginStrings.Key(pluginId, label));
        return definitions.Select(d => d with
        {
            Label = Text(d.Label),
            Columns = d.Columns.Select(c => c with { Label = Text(c.Label) }).ToList(),
        }).ToList();
    }

    private IReadOnlyList<Disciplines.Plugins.PluginReportInfo> PluginReportsOrEmpty()
    {
        try
        {
            return Disciplines.Plugins.PluginReports.Enabled(ctx.Get<IPluginCatalog>());
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Discipline / plugin-report settings as the assistant sees them: the definitions and the values.</summary>
    public sealed record SettingsValues(string Owner, IReadOnlyList<SettingDef> Definitions, IReadOnlyDictionary<string, object?> Values);

    // The patched {values: {...}} back into the types the definitions declare.
    private static Dictionary<string, object?> TypedValues(JsonNode merged, IReadOnlyList<SettingDef> definitions, IReadOnlyDictionary<string, object?> current)
    {
        var values = merged["values"] as JsonObject ?? throw new McpToolException("Patch the 'values' object: {\"values\": {\"key\": value}}.");
        var result = current.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (key, node) in values)
        {
            var def = definitions.FirstOrDefault(d => d.Key == key)
                ?? throw new McpToolException($"Unknown setting '{key}'; settings: {string.Join(", ", definitions.Select(d => d.Key))}.");
            result[key] = node is null ? null : def.Kind switch
            {
                SettingKind.Int => node.GetValueKind() == JsonValueKind.Number ? node.GetValue<int>() : int.Parse(node.ToString()),
                SettingKind.Decimal => node.GetValueKind() == JsonValueKind.Number ? node.GetValue<decimal>() : decimal.Parse(node.ToString().Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture),
                SettingKind.Bool => node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node.GetValue<bool>() : bool.Parse(node.ToString()),
                SettingKind.Choice when !def.Choices.Contains(node.ToString()) =>
                    throw new McpToolException($"'{key}' must be one of: {string.Join(", ", def.Choices)}."),
                SettingKind.Table when current.GetValueOrDefault(key) is { } existing =>
                    node.Deserialize(existing.GetType(), ReadBack),
                _ => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString(),
            };
        }
        return result;
    }

    /// <summary>RFC 7386 JSON merge patch.</summary>
    public static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
            return patch?.DeepClone();
        var result = target is JsonObject t ? (JsonObject)t.DeepClone() : new JsonObject();
        foreach (var (key, value) in patchObject)
        {
            if (value is null)
                result.Remove(key);
            else
                result[key] = MergePatch(result[key], value);
        }
        return result;
    }
}
