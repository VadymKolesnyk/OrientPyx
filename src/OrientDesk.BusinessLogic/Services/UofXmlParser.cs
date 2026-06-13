using System.Globalization;
using System.Xml.Linq;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IUofXmlParser"/>. Reads a UOF participant export (<c>&lt;UOFData&gt;</c> root,
/// a flat list of <c>&lt;Sportsman&gt;</c> records) into <see cref="UofParticipantData"/>.
///
/// The input is already-decoded text — the byte→string decoding (honouring the windows-1251 declared
/// in the file's prolog) happens in the presentation layer before this runs, because
/// <see cref="XDocument.Parse(string)"/> works on a string and ignores the encoding declaration.
/// Field meanings: FIO=full name, Predst=representative, FOUCode=FSOU code, FSOU=membership flag,
/// Birthday=dd.MM.yyyy, Qualification=rank, PAY=payment, Trener=coach (repeated), Region/Club/DUSSH=
/// lookups, Group=group, Chip=chip ("0"=none), ProgEvent="1,2"=day numbers.
/// </summary>
public sealed class UofXmlParser : IUofXmlParser
{
    public UofParticipantData Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new UofXmlFormatException("The file is empty.");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new UofXmlFormatException("The file is not valid XML: " + ex.Message);
        }

        var root = doc.Root;
        if (root is null || !LocalNameIs(root, "UOFData"))
            throw new UofXmlFormatException("The file is not a UOF participant document.");

        var participants = new List<UofParticipant>();
        foreach (var sportsman in root.Elements().Where(e => LocalNameIs(e, "Sportsman")))
            participants.Add(ReadParticipant(sportsman));

        return new UofParticipantData
        {
            Organisation = Trim(ChildValue(root, "Orgs")),
            Participants = participants
        };
    }

    private static UofParticipant ReadParticipant(XElement sportsman)
    {
        // All non-blank <Trener> children, joined — a participant may carry several.
        var coaches = sportsman.Elements()
            .Where(e => LocalNameIs(e, "Trener"))
            .Select(e => Trim(e.Value))
            .Where(c => c.Length > 0);
        var coach = string.Join(", ", coaches);

        var chip = Trim(ChildValue(sportsman, "Chip"));
        // The export writes "0" (and sometimes blank) for "no chip" — normalise both to blank.
        if (chip is "0")
            chip = string.Empty;

        return new UofParticipant
        {
            FullName = Trim(ChildValue(sportsman, "FIO")),
            Representative = Trim(ChildValue(sportsman, "Predst")),
            FsouCode = Trim(ChildValue(sportsman, "FOUCode")),
            IsFsouMember = Trim(ChildValue(sportsman, "FSOU")) == "1",
            BirthDate = ParseDate(ChildValue(sportsman, "Birthday")),
            Rank = Trim(ChildValue(sportsman, "Qualification")),
            Payment = Trim(ChildValue(sportsman, "PAY")),
            Coach = coach,
            Region = Trim(ChildValue(sportsman, "Region")),
            Club = Trim(ChildValue(sportsman, "Club")),
            Dussh = Trim(ChildValue(sportsman, "DUSSH")),
            Group = Trim(ChildValue(sportsman, "Group")),
            Chip = chip,
            DayNumbers = ParseDays(ChildValue(sportsman, "ProgEvent"))
        };
    }

    // ProgEvent is a comma-separated list of 1-based day numbers, e.g. "1,2" or just "1".
    private static IReadOnlyList<int> ParseDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var days = new List<int>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && !days.Contains(n))
                days.Add(n);
        }
        return days;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return null;

        // The export uses dd.MM.yyyy. Treat as a local date (midnight) so it round-trips as a calendar day.
        if (DateTime.TryParseExact(trimmed, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return new DateTimeOffset(date, TimeSpan.Zero);
        return null;
    }

    private static bool LocalNameIs(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => LocalNameIs(e, localName));

    private static string? ChildValue(XElement parent, string localName) =>
        Child(parent, localName)?.Value;

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;
}
