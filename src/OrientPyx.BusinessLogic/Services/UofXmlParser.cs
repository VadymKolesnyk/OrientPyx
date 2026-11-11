using System.Globalization;
using System.Xml.Linq;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

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
/// Relay: a day written with «Е» in ProgEvent («1,2,3Е») is the runner's relay day; there the runner is in the relay
/// group <c>GrupRel</c> («МІКС12»), runs for the team named in <c>Prim</c> and on the leg <c>PsRel</c> (0 = not given).
/// The file has no team numbers — the import creates the teams without them. (<c>RelayLeg</c>, despite its name, is
/// the number of a region's team — «Київська1» — and is not read.)
/// An IOF 3.0 <c>&lt;EntryList&gt;</c> (relay <c>TeamEntry</c> included) is accepted too — see <see cref="IofEntryListReader"/>.
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
        // An IOF 3.0 EntryList (incl. relay TeamEntry) goes through the same participant import.
        if (root is not null && IofEntryListReader.IsEntryList(root))
            return IofEntryListReader.Read(root);
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

        var (days, relayDays) = ParseDays(ChildValue(sportsman, "ProgEvent"));
        // The relay fields mean something only for a runner with a relay day and a relay group («Prim» is a plain
        // note otherwise).
        var relayGroup = relayDays.Count > 0 ? Trim(ChildValue(sportsman, "GrupRel")) : string.Empty;
        var teamName = relayGroup.Length > 0 ? Trim(ChildValue(sportsman, "Prim")) : string.Empty;
        int? leg = relayGroup.Length > 0
                   && int.TryParse(Trim(ChildValue(sportsman, "PsRel")), NumberStyles.None, CultureInfo.InvariantCulture, out var l)
                   && l > 0
            ? l
            : null;

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
            DayNumbers = days,
            RelayDayNumbers = relayDays,
            RelayGroup = relayGroup,
            RelayTeamName = teamName,
            RelayLeg = leg
        };
    }

    // ProgEvent is a comma-separated list of 1-based day numbers, e.g. "1,2" or just "1"; a number followed by «Е»
    // (Cyrillic or Latin, any case) — «3Е» — is a relay day. Any other trailing letters are ignored.
    private static (IReadOnlyList<int> Days, IReadOnlyList<int> RelayDays) ParseDays(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ([], []);

        var days = new List<int>();
        var relayDays = new List<int>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var digits = 0;
            while (digits < part.Length && char.IsAsciiDigit(part[digits]))
                digits++;
            if (digits == 0
                || !int.TryParse(part[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
                continue;
            if (!days.Contains(n))
                days.Add(n);
            var suffix = part[digits..].Trim();
            if (suffix.Length > 0 && (suffix[0] is 'Е' or 'е' or 'E' or 'e') && !relayDays.Contains(n))
                relayDays.Add(n);
        }
        return (days, relayDays);
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
