using System.Globalization;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Operations;

/// <summary>
/// The non-UI half of the CSV / Excel participant import: guessing which column feeds which field (the remembered
/// mapping first, else header hints) and turning the mapped rows into <see cref="UofParticipantData"/>. The import
/// modal (<c>CsvMappingViewModel</c>) and the assistant's <c>import_file</c> share it.
/// </summary>
public static class CsvParticipantMapper
{
    /// <summary>Lowercase, whitespace-free substrings (Ukrainian + English) hinting at each built-in field.</summary>
    public static string[] Hints(ImportTarget target) => target.Key switch
    {
        nameof(ImportTarget.FullName) => ["прізвище", "ім'я", "піб", "fio", "name", "учасник"],
        nameof(ImportTarget.Number) => ["номер", "№", "нагрудний", "стартовийномер", "bib", "number", "startno", "start#"],
        nameof(ImportTarget.Team) => ["команд", "team", "екіпаж"],
        nameof(ImportTarget.BirthDate) => ["народж", "рікнар", "датанар", "birth", "born", "ддр", "дн"],
        nameof(ImportTarget.Group) => ["група", "group", "категор"],
        nameof(ImportTarget.Region) => ["регіон", "область", "region"],
        nameof(ImportTarget.Club) => ["клуб", "club", "колектив", "команд"],
        nameof(ImportTarget.Dussh) => ["дюсш", "сдюшор", "школа", "dussh"],
        nameof(ImportTarget.Chip) => ["чіп", "чип", "card", "chip", "si"],
        nameof(ImportTarget.Rank) => ["розряд", "кваліфік", "rank", "qualif"],
        nameof(ImportTarget.Representative) => ["представник", "predst"],
        nameof(ImportTarget.FsouCode) => ["кодфсоу", "foucode", "fsoucode", "кодфоу"],
        nameof(ImportTarget.IsFsouMember) => ["членфсоу", "членфоу", "fsou"],
        nameof(ImportTarget.Payment) => ["оплата", "сплата", "внесок", "payment", "pay"],
        nameof(ImportTarget.Coach) => ["тренер", "trener", "coach"],
        "relay.teamNumber" => ["номеркоманди", "№команди", "teamno", "teamnumber", "teamnr"],
        "relay.teamName" => ["назвакоманди", "teamname"],
        "relay.leg" => ["етап", "leg"],
        _ => [],
    };

    /// <summary>The first column whose caption contains one of the target's hints; -1 when nothing looks right.</summary>
    public static int Guess(ImportTarget target, IReadOnlyList<string> header)
    {
        var hints = Hints(target);
        for (var i = 0; i < header.Count; i++)
        {
            var caption = Normalize(header[i]);
            if (caption.Length > 0 && hints.Any(h => caption.Contains(h, StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }

    /// <summary>An additional field's column: the one headed exactly like it (label, key, a plugin's import name); -1 none.</summary>
    public static int GuessField(ImportField field, IReadOnlyList<string> header)
    {
        for (var i = 0; i < header.Count; i++)
            if (field.Matches(header[i]))
                return i;
        return -1;
    }

    /// <summary>The column a remembered mapping names for <paramref name="target"/>; -1 when it names none.</summary>
    public static int FromTemplate(ImportTarget target, IReadOnlyDictionary<string, string> template, IReadOnlyList<string> header)
    {
        if (!template.TryGetValue(target.Key, out var caption))
            return -1;
        for (var i = 0; i < header.Count; i++)
            if (TextKey.Same(header[i], caption))
                return i;
        return -1;
    }

    /// <summary>
    /// The mapping the modal would pre-select: with a remembered <paramref name="template"/> only what it names, else
    /// every built-in target (and the relay ones on a relay day) by header hints and every additional field by name.
    /// </summary>
    public static Dictionary<ImportTarget, int> AutoMap(
        IReadOnlyList<string> header,
        IReadOnlyList<ImportField> fields,
        IReadOnlyDictionary<string, string>? template,
        bool relay)
    {
        var map = new Dictionary<ImportTarget, int>();
        var useTemplate = template is { Count: > 0 };
        foreach (var target in ImportTarget.Builtins.Concat(relay ? ImportTarget.Relay : []))
        {
            var i = useTemplate ? FromTemplate(target, template!, header) : Guess(target, header);
            if (i >= 0)
                map[target] = i;
        }
        foreach (var field in fields)
        {
            var target = ImportTarget.Field(field.Key);
            var i = useTemplate ? FromTemplate(target, template!, header) : GuessField(field, header);
            if (i >= 0)
                map[target] = i;
        }
        return map;
    }

    /// <summary>
    /// Turns the mapped rows into participant data. Every athlete runs on all existing days. On a relay day the team comes
    /// from «Номер команди» (+ «Етап»), or from a leg number «100.2» in the number column, which then is not written as the
    /// participant's own number (Р-23).
    /// </summary>
    public static UofParticipantData BuildData(
        CsvParticipantData file,
        IReadOnlyDictionary<ImportTarget, int> map,
        IReadOnlyList<EventDay> days,
        bool relay = false)
    {
        var dayNumbers = days.Select(d => d.Number).OrderBy(n => n).ToList();
        var participants = new List<UofParticipant>(file.Rows.Count);

        foreach (var row in file.Rows)
        {
            var name = Cell(row, map, ImportTarget.FullName);
            // Skip rows with no name — they can't become a participant.
            if (name.Length == 0)
                continue;

            var chip = Cell(row, map, ImportTarget.Chip);
            if (chip is "0")
                chip = string.Empty;

            var number = Cell(row, map, ImportTarget.Number);
            int? teamNumber = null;
            int? leg = int.TryParse(Cell(row, map, ImportTarget.RelayLeg), out var l) && l > 0 ? l : null;
            if (RelayBib.TryParse(Cell(row, map, ImportTarget.RelayTeamNumber), out var t, out var tl) && t > 0)
            {
                teamNumber = t;
                leg ??= tl;
            }
            else if (relay && RelayBib.TryParse(number, out t, out tl) && tl is not null && t > 0)
            {
                teamNumber = t;
                leg ??= tl;
                number = string.Empty;
            }

            participants.Add(new UofParticipant
            {
                FullName = name,
                Number = number,
                Team = Cell(row, map, ImportTarget.Team),
                BirthDate = ParseDate(Cell(row, map, ImportTarget.BirthDate)),
                Region = Cell(row, map, ImportTarget.Region),
                Club = Cell(row, map, ImportTarget.Club),
                Dussh = Cell(row, map, ImportTarget.Dussh),
                Group = Cell(row, map, ImportTarget.Group),
                Chip = chip,
                Rank = Cell(row, map, ImportTarget.Rank),
                Representative = Cell(row, map, ImportTarget.Representative),
                FsouCode = Cell(row, map, ImportTarget.FsouCode),
                IsFsouMember = ParseBool(Cell(row, map, ImportTarget.IsFsouMember)),
                Payment = Cell(row, map, ImportTarget.Payment),
                Coach = Cell(row, map, ImportTarget.Coach),
                DayNumbers = dayNumbers,
                Fields = FieldCells(row, map),
                RelayTeamNumber = teamNumber,
                RelayTeamName = Cell(row, map, ImportTarget.RelayTeamName),
                RelayLeg = teamNumber is null ? null : leg,
            });
        }

        return new UofParticipantData { Participants = participants };
    }

    // The additional-field cells of a row, by field key (normalised by the import, Р-18/Р-19).
    private static IReadOnlyDictionary<string, string> FieldCells(
        IReadOnlyList<string> row,
        IReadOnlyDictionary<ImportTarget, int> map)
    {
        var fields = new Dictionary<string, string>();
        foreach (var (target, i) in map)
            if (target.FieldKey is { Length: > 0 } key && i >= 0 && i < row.Count)
                fields[key] = row[i].Trim();
        return fields;
    }

    private static string Cell(IReadOnlyList<string> row, IReadOnlyDictionary<ImportTarget, int> map, ImportTarget field) =>
        map.TryGetValue(field, out var i) && i >= 0 && i < row.Count ? row[i].Trim() : string.Empty;

    // Treats common truthy spellings as a "yes"; everything else (incl. blank) is false.
    private static readonly string[] TruthyValues = ["1", "+", "так", "yes", "y", "true"];

    private static bool ParseBool(string value) =>
        TruthyValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    // Accepts the common date spellings (dd.MM.yyyy / yyyy-MM-dd / dd/MM/yyyy) and a bare year.
    private static DateTimeOffset? ParseDate(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return null;

        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new DateTimeOffset(date, TimeSpan.Zero);

        // A bare 4-digit year → 1 January of that year (registration files sometimes carry only the year).
        if (trimmed.Length == 4 && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && year is >= 1900 and <= 2200)
            return new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero);

        return null;
    }

    private static string Normalize(string s) =>
        new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}
