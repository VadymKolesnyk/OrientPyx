using System.Globalization;
using System.Text.Json;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>export</c>: the program's own documents with the settings the pages saved (a blank header field folded to the
/// competition / day default, as the pages do), the same builders and writers, the labels from the window. Written to
/// the competition's exports folder of THIS computer (on a station: its local folder, never the host's) unless the user
/// named another folder — then it is a risky change.
/// </summary>
public sealed class ExportTools(McpContext ctx) : IMcpToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = ["export"];

    public async Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken)
    {
        var competition = ctx.RequireCompetition(args);
        var kind = args.ReqStr("kind").Trim().ToLowerInvariant();
        if (!McpToolCatalog.ExportKinds.Contains(kind))
            throw new McpToolException($"Unknown export '{kind}'; kinds: {string.Join(", ", McpToolCatalog.ExportKinds)}.");
        var day = await ctx.DayAsync(competition, args, cancellationToken);
        var days = await ctx.DaysAsync(competition, cancellationToken);
        var report = kind == "plugin_report" ? PluginReport(args) : null;
        var dayless = kind is "summary_protocol" or "participants_csv" or "participants_xlsx" || report?.Scope == ReportScope.Competition;
        var labels = ctx.Ui.Labels;
        var files = ctx.Get<IMcpFileAccess>();

        var extension = kind switch
        {
            "splits_html" => "html",
            "iof_results" or "iof_start_list" => "xml",
            "participants_csv" => "csv",
            "participants_xlsx" => "xlsx",
            _ => "docx",
        };
        var namingKind = kind is "participants_csv" or "participants_xlsx" ? "participants" : report is not null ? "key:" + report.NameKey : kind;
        var fileName = args.Str("file_name")?.Trim() is { Length: > 0 } given
            ? (Path.HasExtension(given) ? given : $"{given}.{extension}")
            : labels.FileName(namingKind, competition.Name, extension, dayless ? null : day, days.Count > 1,
                dayless ? days.Where(d => d.Date is not null).Max(d => d.Date) : null);

        var customFolder = args.Str("folder")?.Trim();
        var folder = string.IsNullOrEmpty(customFolder) ? DefaultFolder(competition, dayless ? null : day) : McpFilePolicy.Full(customFolder);
        var target = McpFilePolicy.ExportPath(files, folder, fileName, args.Flag("overwrite"));

        var summary = ChangeSummary.Of("Mcp.Change.Export", $"Write {Path.GetFileName(target)}", Path.GetFileName(target), target);
        var cls = string.IsNullOrEmpty(customFolder) ? McpToolClass.File : McpToolClass.Risky;
        var write = new McpWrite("export", cls, competition, dayless ? null : day, summary, Undoable: false);
        return await ctx.Writer.RunAsync(write, async t =>
        {
            var bytes = report is not null
                ? await PluginReportAsync(report, dayless ? null : day, t)
                : await BuildAsync(kind, competition, day, days, labels, t);
            await files.WriteAsync(target, bytes, t);
            return new { path = target, bytes = bytes.Length, kind, report = report?.Id, day = dayless ? (int?)null : day.Number };
        }, call, cancellationToken);
    }

    // A switched-on plugin's report (Плагіни → the plugin → the report).
    private PluginReportInfo PluginReport(McpArgs args)
    {
        var enabled = PluginReports.Enabled(ctx.Find<IPluginCatalog>());
        var id = args.Str("report")?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new McpToolException(enabled.Count == 0
                ? "No switched-on plugin has reports (plugins are switched on only in OrientPyx: «Плагіни → Керування плагінами»)."
                : "'report' is required for kind=plugin_report; reports: " +
                  string.Join("; ", enabled.Select(r => $"{r.Id} ({ctx.Ui.Text(r.NameKey)}, {(r.Scope == ReportScope.Competition ? "whole competition" : "per day")})")) + ".");
        return enabled.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpToolException($"No report '{id}' of a switched-on plugin; reports: " +
                (enabled.Count == 0 ? "none" : string.Join(", ", enabled.Select(r => r.Id))) + ".");
    }

    private async Task<byte[]> PluginReportAsync(PluginReportInfo report, EventDay? day, CancellationToken ct)
    {
        var ui = ctx.Ui;
        var labels = new PluginReportLabels(
            ChiefJudgeLabel: ui.Text("Protocols.ChiefJudge"),
            ChiefSecretaryLabel: ui.Text("Protocols.ChiefSecretary"),
            JuryLabel: ui.Text("Protocols.Jury"),
            FooterSoftwareName: ui.Text("Protocols.Footer.Software"),
            FooterGeneratedLabel: ui.Text("Protocols.Footer.Generated"),
            FooterPageLabel: ui.Text("Protocols.Footer.Page"),
            Text: ui.Text);
        var output = await ctx.Editor.BuildPluginReportAsync(report.Id, day?.Id, labels, ct);
        if (output.Error is { } error)
            throw new McpToolException($"The plugin could not build the report «{ui.Text(report.NameKey)}»: {error}. Nothing was written.");
        var document = output.Document
            ?? throw new McpToolException("The report has nothing to export (no competition / day). Nothing was written.");
        return ctx.Get<IResultProtocolWriter>().Write(document);
    }

    private string DefaultFolder(EventSummary competition, EventDay? day)
    {
        var eventFiles = ctx.Files;
        var local = eventFiles.LocalReadoutDirectory(competition.FolderPath, competition.Identifier, day?.Number);
        // Without a day the local folder is «chips»: exports of the whole competition go beside it.
        var baseFolder = day is null ? Path.GetDirectoryName(local) ?? local : local;
        return Path.Combine(baseFolder, "exports");
    }

    private async Task<byte[]> BuildAsync(string kind, EventSummary competition, EventDay day, IReadOnlyList<EventDay> days, IOutputLabels labels, CancellationToken ct)
    {
        var editor = ctx.Editor;
        var app = ctx.Get<IAppSettingsService>();
        var info = await editor.GetInfoAsync(ct);
        var defaults = Defaults(info, competition, day, labels);
        switch (kind)
        {
            case "result_protocol":
            {
                var settings = Clone(await editor.GetResultProtocolSettingsAsync(day.Id, ct) ?? await app.GetResultProtocolSettingsAsync(ct));
                Fold(settings.CompetitionName, v => settings.CompetitionName = v, defaults.Name);
                Fold(settings.Subtitle, v => settings.Subtitle = v, defaults.Subtitle);
                Fold(settings.Venue, v => settings.Venue = v, defaults.Venue);
                Fold(settings.DateText, v => settings.DateText = v, defaults.Date);
                Fold(settings.CompetitionType, v => settings.CompetitionType = v, defaults.Type);
                var data = await editor.GetResultProtocolDataAsync(day.Id, ct);
                return ctx.Get<IResultProtocolWriter>().Write(ctx.Get<IResultProtocolBuilder>().Build(data, settings, labels.ResultProtocol()));
            }
            case "start_protocol" or "judges_start_protocol":
            {
                var protocolKind = kind == "judges_start_protocol" ? StartProtocolKind.Judges : StartProtocolKind.Regular;
                var settings = Clone(await editor.GetStartProtocolSettingsAsync(day.Id, protocolKind, ct) ?? await app.GetStartProtocolSettingsAsync(protocolKind, ct));
                Fold(settings.CompetitionName, v => settings.CompetitionName = v, defaults.Name);
                Fold(settings.Subtitle, v => settings.Subtitle = v, defaults.Subtitle);
                Fold(settings.Venue, v => settings.Venue = v, defaults.Venue);
                Fold(settings.DateText, v => settings.DateText = v, defaults.Date);
                Fold(settings.CompetitionType, v => settings.CompetitionType = v, defaults.Type);
                var data = await editor.GetStartProtocolDataAsync(day.Id, ct);
                return ctx.Get<IResultProtocolWriter>().Write(
                    ctx.Get<IStartProtocolBuilder>().Build(data, settings, protocolKind, labels.StartProtocol(protocolKind)));
            }
            case "summary_protocol":
            {
                var settings = Clone(await ctx.SummarySettingsAsync(competition, ct));
                var whole = Defaults(info, competition, null, labels);
                Fold(settings.CompetitionName, v => settings.CompetitionName = v, whole.Name);
                Fold(settings.Subtitle, v => settings.Subtitle = v, whole.Subtitle);
                Fold(settings.Venue, v => settings.Venue = v, whole.Venue);
                var data = await editor.GetSummaryProtocolDataAsync(ct);
                return ctx.Get<ISummaryProtocolWriter>().Write(ctx.Get<ISummaryProtocolBuilder>().Build(data, settings, labels.SummaryProtocol()));
            }
            case "splits_html":
            {
                var data = await editor.GetDaySplitsExportDataAsync(day.Id, ct);
                var header = new SplitExportHeader(string.Empty, defaults.Name, defaults.Type, defaults.Venue, defaults.Date);
                return ctx.Get<ISplitHtmlWriter>().Write(ctx.Get<ISplitExportBuilder>().Build(data, header, labels.Splits()));
            }
            case "iof_results" or "iof_start_list":
            {
                var listKind = kind == "iof_results" ? IofListKind.ResultList : IofListKind.StartList;
                var saved = await editor.GetIofExportSettingsAsync(ct) ?? new IofExportSettings();
                var data = await editor.GetIofExportDataAsync([day.Id], listKind, ct);
                if (data.Summary is not null && data.SummarySettings is null)
                    data = data with { SummarySettings = await ctx.SummarySettingsAsync(competition, ct) };
                var dayLabel = ctx.Ui.Text("Header.Day");
                var options = new IofExportOptions(
                    null, saved.Organisation, saved.IncludeBirthDates,
                    days.Count > 1 ? $"{defaults.Name} — {dayLabel} {day.Number}" : defaults.Name,
                    Creator: $"OrientPyx {McpContext.AppVersion}", CreateTime: DateTimeOffset.Now, DayLabel: dayLabel);
                var builder = ctx.Get<IIofExportBuilder>();
                var writer = ctx.Get<IIofXmlWriter>();
                return listKind == IofListKind.ResultList ? writer.Write(builder.BuildResultList(data, options)) : writer.Write(builder.BuildStartList(data, options));
            }
            case "statement":
            {
                var settings = Clone(await editor.GetStatementSettingsAsync(ct) ?? await app.GetStatementSettingsAsync(ct));
                Fold(settings.CompetitionName, v => settings.CompetitionName = v, defaults.Name);
                Fold(settings.Subtitle, v => settings.Subtitle = v, defaults.Subtitle);
                Fold(settings.Venue, v => settings.Venue = v, defaults.Venue);
                Fold(settings.DateText, v => settings.DateText = v, defaults.Date);
                var ids = (await editor.GetParticipantDayRowsAsync(ct)).Select(r => r.ParticipantId).Distinct().ToList();
                var data = await editor.GetStatementDataAsync(day.Id, ids, ct);
                return ctx.Get<IResultProtocolWriter>().Write(ctx.Get<IStatementBuilder>().Build(data, settings, labels.Statement(), string.Empty));
            }
            case "participants_csv" or "participants_xlsx":
            {
                var format = kind == "participants_csv" ? ExportFormat.Csv : ExportFormat.Excel;
                var writer = ctx.Get<IEnumerable<ITabularWriter>>().FirstOrDefault(w => w.Format == format)
                    ?? throw new McpToolException($"No {format} writer.");
                return writer.Write(await ParticipantsTableAsync(days, ct));
            }
            default:
                throw new McpToolException($"Unknown export '{kind}'.");
        }
    }

    // The roster as a flat table: identity columns, then group / chip / start per day (the program's own headers).
    private async Task<CsvParticipantData> ParticipantsTableAsync(IReadOnlyList<EventDay> days, CancellationToken ct)
    {
        var ui = ctx.Ui;
        var roster = await ctx.Editor.GetParticipantRosterAsync(ct);
        var fields = await ctx.Editor.GetCustomFieldsAsync(ct);
        string H(string key) => ui.Text("Participants.Col." + key);
        var dayLabel = ui.Text("Header.Day");
        var header = new List<string>
        {
            H("Number"), H("FullName"), H("BirthDate"), H("Club"), H("Region"), H("Dussh"), H("Rank"), H("Coach"),
            H("Representative"), H("Team"), H("FsouCode"), H("Payment"), H("Note"), H("TotalFee"),
        };
        header.AddRange(fields.Select(f => f.Label));
        foreach (var d in days)
            header.AddRange([$"{dayLabel} {d.Number}: {H("Group")}", $"{dayLabel} {d.Number}: {H("Chip")}", $"{dayLabel} {d.Number}: {H("StartTime")}"]);
        var rows = roster.OrderBy(p => p.FullName, StringComparer.CurrentCulture).Select(p =>
        {
            var row = new List<string>
            {
                p.Number, p.FullName, p.BirthDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "", p.ClubName, p.RegionName,
                p.DusshName, p.Rank, p.Coach, p.Representative, p.Team, p.FsouCode, p.Payment, p.Note,
                p.TotalEntryFee.ToString(CultureInfo.InvariantCulture),
            };
            row.AddRange(fields.Select(f => p.Fields.GetValueOrDefault(f.Key) ?? string.Empty));
            foreach (var d in days)
            {
                var cell = p.Days.FirstOrDefault(c => c.DayId == d.Id && c.IsMember);
                row.AddRange([cell?.GroupName ?? "", cell?.Chip ?? "", cell?.StartTime is { } st ? McpTime.Clock(st) : ""]);
            }
            return (IReadOnlyList<string>)row;
        }).ToList();
        return new CsvParticipantData { Header = header, Rows = rows };
    }

    private sealed record HeaderDefaults(string Name, string Subtitle, string Venue, string Date, string Type);

    // The pages' header placeholders: the day's values first, then the competition's.
    private static HeaderDefaults Defaults(CompetitionInfo? info, EventSummary competition, EventDay? day, IOutputLabels labels)
    {
        var name = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name.Trim() : competition.Name.Trim();
        var venue = !string.IsNullOrWhiteSpace(day?.Venue) ? day!.Venue.Trim() : info?.Venue?.Trim() ?? string.Empty;
        var date = (day?.Date ?? info?.StartDate) is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty;
        var type = day is null ? string.Empty : labels.DisciplineName(day.DefaultDiscipline);
        return new HeaderDefaults(name, info?.Organisation?.Trim() ?? string.Empty, venue, date, type);
    }

    private static void Fold(string current, Action<string> set, string fallback)
    {
        if (string.IsNullOrWhiteSpace(current))
            set(fallback);
    }

    // A private copy: the stored settings object is never changed by the folding.
    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Serialization.RpcJson.Options), Serialization.RpcJson.Options)!;
}
