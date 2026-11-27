using System.Xml;
using System.Xml.Linq;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Operations;
using OrientPyx.BusinessLogic.Services.Text;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// <c>inspect_file</c>: a look into a file the user named before importing it — what it is (IOF courses / entries,
/// УОФ, a read-out file of which timing system, a participants table), its size in records, a table's columns, first
/// rows and the column mapping the import would use. Reads only.
/// </summary>
public sealed class FileTools(McpContext ctx) : IMcpToolHandler
{
    private const int SampleRows = 5;

    public IReadOnlyCollection<string> Tools { get; } = ["inspect_file"];

    public Task<object?> InvokeAsync(string tool, McpArgs args, McpCall call, CancellationToken cancellationToken) => tool switch
    {
        "inspect_file" => InspectAsync(args, cancellationToken),
        _ => throw new McpToolException($"Unknown tool '{tool}'."),
    };

    private async Task<object?> InspectAsync(McpArgs args, CancellationToken cancellationToken)
    {
        var files = ctx.Get<IMcpFileAccess>();
        var full = McpFilePolicy.Full(args.ReqStr("path"));
        var ext = Path.GetExtension(full).ToLowerInvariant();
        var known = McpFilePolicy.ImportExtensions.Values.SelectMany(e => e).ToHashSet();
        if (!known.Contains(ext))
            throw new McpToolException($"The program imports {string.Join(", ", known.Order())} files, not '{ext}'.");
        var size = files.SizeOf(full) ?? throw new McpToolException($"There is no file '{full}'. Ask the user for the exact path.");
        if (size > McpFilePolicy.MaxImportBytes)
            throw new McpToolException($"The file is {size / 1024 / 1024} MB; at most 50 MB can be imported.");
        var bytes = await files.ReadAsync(full, cancellationToken);

        object details = ext switch
        {
            ".xml" => Xml(bytes),
            ".xlsx" => await TableAsync(Read(() => ctx.Get<ISpreadsheetParser>().Parse(bytes)), args, cancellationToken),
            _ => await TextAsync(bytes, args, cancellationToken),
        };
        return new { path = full, size_kb = Math.Max(1, (size + 1023) / 1024), details };
    }

    // ── XML: the root says what it is

    private static object Xml(byte[] bytes)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(XmlEncodingReader.DecodeXml(bytes));
        }
        catch (XmlException ex)
        {
            throw new McpToolException($"The file is not valid XML: {ex.Message}");
        }
        var root = doc.Root ?? throw new McpToolException("The XML file is empty.");
        int Count(string name) => root.Descendants().Count(e => e.Name.LocalName == name);
        var version = root.Attribute("iofVersion")?.Value;
        return root.Name.LocalName switch
        {
            "CourseData" => new
            {
                format = "IOF XML " + (version ?? "2.0"),
                content = "controls and courses",
                import_kind = "iof_courses",
                races = Math.Max(1, Count("RaceCourseData")),
                controls = root.Descendants().Count(e => e.Name.LocalName == "Control" && e.Parent?.Name.LocalName is "RaceCourseData" or "CourseData"),
                courses = Count("Course"),
                course_names = root.Descendants().Where(e => e.Name.LocalName == "Course")
                    .Select(c => c.Elements().FirstOrDefault(x => x.Name.LocalName is "Name" or "CourseName")?.Value.Trim())
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(40).ToList(),
            } as object,
            "EntryList" => new
            {
                format = "IOF XML " + (version ?? "2.0"),
                content = "entry list",
                import_kind = "iof_entries",
                people = Count("PersonEntry") + root.Descendants().Count(e => e.Name.LocalName == "TeamMemberEntry"),
                teams = Count("TeamEntry"),
            },
            "UOFData" => new
            {
                format = "УОФ XML",
                content = "participants",
                import_kind = "uof",
                people = root.Elements().Count(e => e.Name.LocalName == "Sportsman"),
            },
            "ResultList" or "StartList" => new
            {
                format = "IOF XML " + (version ?? "2.0"),
                content = root.Name.LocalName == "ResultList" ? "result list" : "start list",
                import_kind = (string?)null,
                classes = Count(root.Name.LocalName == "ResultList" ? "ClassResult" : "ClassStart"),
                note = "The program does not import result / start lists. Courses come as IOF CourseData, entries as IOF EntryList or УОФ.",
            },
            var other => new
            {
                format = "XML",
                content = "unknown",
                import_kind = (string?)null,
                root = other,
                note = "Not a file the program imports (IOF CourseData / EntryList or УОФ expected).",
            },
        };
    }

    // ── Text: a read-out file of a timing system, else a table

    private async Task<object> TextAsync(byte[] bytes, McpArgs args, CancellationToken cancellationToken)
    {
        var content = CsvEncodingReader.Decode(bytes);
        var selector = ctx.Get<IReadoutParserSelector>();
        var current = await selector.GetCurrentAsync(cancellationToken);
        foreach (var (name, type) in new[] { ("sportident", ReadoutType.SportIdent), ("sporttime", ReadoutType.SportTime) })
        {
            var parser = selector.For(type);
            if (!parser.CanParse(content))
                continue;
            ChipReadData data;
            try
            {
                data = parser.Parse(content);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new McpToolException($"The file looks like a {name} read-out file but cannot be read: {ex.Message}");
            }
            return new
            {
                format = name,
                content = "chip read-outs",
                import_kind = "readouts (finish) or rental_chips",
                matches_program_setting = ReferenceEquals(parser, current) ? true : (bool?)null,
                note = ReferenceEquals(parser, current)
                    ? null
                    : $"«Тип відмітки» in OrientPyx is another timing system: import with format=\"{name}\".",
                records = data.Records.Count,
                chips = data.Records.Select(r => r.ChipNumber.Trim()).Where(c => c.Length > 0).Distinct().Count(),
                first_chips = data.Records.Select(r => r.ChipNumber.Trim()).Where(c => c.Length > 0).Take(SampleRows).ToList(),
            };
        }
        return await TableAsync(Read(() => ctx.Get<ICsvParser>().Parse(content)), args, cancellationToken);
    }

    private async Task<object> TableAsync(CsvParticipantData table, McpArgs args, CancellationToken cancellationToken)
    {
        object? mapping = null;
        var saved = false;
        if (ctx.Session.CurrentEvent is { } competition)
        {
            var day = await ctx.DayAsync(competition, args, cancellationToken);
            using (McpContext.Pin(competition, day))
            {
                var editor = ctx.Editor;
                var fields = await editor.GetImportFieldsAsync(cancellationToken);
                var json = await editor.GetImportMappingAsync(ImportMapping.HeaderSetHash(table.Header), cancellationToken);
                saved = json is not null;
                var map = CsvParticipantMapper.AutoMap(table.Header, fields, json is null ? null : ImportMapping.Parse(json),
                    await editor.CurrentDayHasRelayAsync(cancellationToken));
                mapping = map.Where(kv => kv.Value >= 0 && kv.Value < table.Header.Count)
                    .OrderBy(kv => kv.Value)
                    .ToDictionary(kv => table.Header[kv.Value], kv => kv.Key.Key);
            }
        }
        var unmapped = mapping is IReadOnlyDictionary<string, string> m ? table.Header.Where(h => !m.ContainsKey(h)).ToList() : null;
        return new
        {
            format = "table",
            content = "participants table",
            import_kind = "participants_table",
            columns = table.Header,
            rows = table.Rows.Count,
            first_rows = table.Rows.Take(SampleRows).Select(r => table.Header
                .Select((h, i) => (h, v: i < r.Count ? r[i] : string.Empty))
                .Where(x => x.v.Trim().Length > 0)
                .ToDictionary(x => x.h, x => x.v)).ToList(),
            mapping,
            mapping_saved = saved ? true : (bool?)null,
            unmapped_columns = unmapped is { Count: > 0 } ? unmapped : null,
            note = mapping is null
                ? null
                : "mapping = what import_file uses by default (column → field); pass 'mapping' to import_file to change it.",
        };
    }

    private static CsvParticipantData Read(Func<CsvParticipantData> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpToolException($"The table cannot be read: {ex.Message}");
        }
    }
}
