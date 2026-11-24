using System.Globalization;
using System.Xml.Linq;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Reads an IOF data-standard 3.0 <c>&lt;EntryList&gt;</c> (entries exported from OEvent/OCAD/Eventor) or
/// <c>&lt;StartList&gt;</c> into the same <see cref="UofParticipantData"/> the participant import consumes.
/// Relay entries (<c>&lt;TeamEntry&gt;</c>) give one participant per <c>&lt;TeamEntryPerson&gt;</c> carrying the team
/// number (the team's <c>BibNumber</c> only; a team without one stays without a number and is matched by name, like a
/// UOF team), the team name and the person's <c>&lt;Leg&gt;</c>; individual entries (<c>&lt;PersonEntry&gt;</c>) give a
/// plain participant. The class is the group.
/// Days: <c>PersonEntry/RaceNumber</c> and <c>TeamEntry/Race</c> (0..n) become <see cref="UofParticipant.DayNumbers"/>
/// (a team member inherits the team's); none = every day, the import's rule. The relay team/leg is applied on the relay
/// days only.
/// Person: <c>Id type="FSOU"</c> is the FSOU code (an untyped <c>Id</c> too, when it is the person's only one);
/// <c>BirthDate</c> is an xsd:date (its first 10 characters). Organisation: <c>type="NationalRegion"</c> is the region,
/// any other the club. Several <c>ControlCard</c>: the SportIdent one, else one without <c>punchingSystem</c>, else the
/// first; "0" is none.
/// A <c>&lt;StartList&gt;</c> (what the program's own export writes) is read the same way from its standard places:
/// <c>Start/BibNumber</c>, <c>Start/ControlCard</c>, <c>Start/StartTime</c> (the clock in its own offset),
/// <c>TeamStart/BibNumber</c>, <c>Start/Leg</c> (else the leg of a member bib «100.2» / «101-2» / «101/2»). Several
/// <c>Start</c> elements (<c>raceNumber</c>) give the days; the values are the first one's — a start time per day is
/// not read.
/// </summary>
internal static class IofEntryListReader
{
    public static bool IsEntryList(XElement root) => root.Name.LocalName == "EntryList";

    public static bool IsStartList(XElement root) => root.Name.LocalName == "StartList";

    public static UofParticipantData Read(XElement root)
    {
        var participants = new List<UofParticipant>();

        foreach (var team in Children(root, "TeamEntry"))
        {
            // The number is the team's BibNumber only; the entry Id is the registration system's, not a bib.
            var number = PositiveInt(Value(team, "BibNumber"));
            var teamName = Value(team, "Name");
            var group = Value(Child(team, "Class"), "Name");
            var days = Days(Children(team, "Race").Select(r => r.Value));

            foreach (var member in Children(team, "TeamEntryPerson"))
            {
                var person = Child(member, "Person");
                var name = FullName(person);
                if (name.Length == 0)
                    continue;
                participants.Add(TeamMember(
                    person, name, Child(member, "Organisation") ?? Child(team, "Organisation"), group, Chip(member),
                    number, teamName, PositiveInt(Value(member, "Leg")), days, UofXmlParser.RawValues(person, member),
                    startTime: null));
            }
        }

        foreach (var entry in Children(root, "PersonEntry"))
        {
            var person = Child(entry, "Person");
            var name = FullName(person);
            if (name.Length == 0)
                continue;
            var (club, region) = Organisation(Child(entry, "Organisation"));
            participants.Add(new UofParticipant
            {
                FullName = name,
                BirthDate = BirthDate(person),
                FsouCode = FsouCode(person),
                Club = club,
                Region = region,
                Group = Value(Child(entry, "Class"), "Name"),
                Chip = Chip(entry),
                Number = Value(entry, "BibNumber"),
                DayNumbers = Days(Children(entry, "RaceNumber").Select(r => r.Value)),
                Raw = UofXmlParser.RawValues(person, entry)
            });
        }

        return new UofParticipantData { Participants = participants };
    }

    public static UofParticipantData ReadStartList(XElement root)
    {
        var participants = new List<UofParticipant>();

        foreach (var classStart in Children(root, "ClassStart"))
        {
            var group = Value(Child(classStart, "Class"), "Name");

            foreach (var entry in Children(classStart, "PersonStart"))
            {
                var person = Child(entry, "Person");
                var name = FullName(person);
                if (name.Length == 0)
                    continue;
                var starts = Children(entry, "Start").ToList();
                var start = starts.FirstOrDefault();
                var (club, region) = Organisation(Child(entry, "Organisation"));
                participants.Add(new UofParticipant
                {
                    FullName = name,
                    BirthDate = BirthDate(person),
                    FsouCode = FsouCode(person),
                    Club = club,
                    Region = region,
                    Group = group,
                    Chip = Chip(start),
                    Number = Value(start, "BibNumber"),
                    StartTime = StartTime(start),
                    DayNumbers = StartDays(starts),
                    Raw = UofXmlParser.RawValues(person, entry)
                });
            }

            foreach (var team in Children(classStart, "TeamStart"))
            {
                var number = PositiveInt(Value(team, "BibNumber"));
                var teamName = Value(team, "Name");
                foreach (var member in Children(team, "TeamMemberStart"))
                {
                    var person = Child(member, "Person");
                    var name = FullName(person);
                    if (name.Length == 0)
                        continue;
                    var starts = Children(member, "Start").ToList();
                    var start = starts.FirstOrDefault();
                    var leg = PositiveInt(Value(start, "Leg")) ?? MemberBibLeg(Value(start, "BibNumber"));
                    participants.Add(TeamMember(
                        person, name, Child(member, "Organisation") ?? Child(team, "Organisation"), group, Chip(start),
                        number, teamName, leg, StartDays(starts), UofXmlParser.RawValues(person, member),
                        StartTime(start)));
                }
            }
        }

        return new UofParticipantData { Participants = participants };
    }

    // One member of a relay team. A team without a number but with a name is still a relay row: the import matches it by
    // name on the relay days (RelayOnImportedDays — the days the row is entered on, like a UOF relay event).
    private static UofParticipant TeamMember(
        XElement? person, string name, XElement? organisation, string group, string chip, int? number, string teamName,
        int? leg, IReadOnlyList<int> days, IReadOnlyDictionary<string, string> raw, TimeSpan? startTime)
    {
        var (club, region) = Organisation(organisation);
        var byName = number is null && teamName.Length > 0;
        return new UofParticipant
        {
            FullName = name,
            BirthDate = BirthDate(person),
            FsouCode = FsouCode(person),
            Club = club,
            Region = region,
            Group = group,
            Chip = chip,
            Team = teamName,
            RelayTeamNumber = number,
            RelayTeamName = teamName,
            RelayGroup = byName ? group : string.Empty,
            RelayOnImportedDays = byName,
            RelayLeg = leg,
            StartTime = startTime,
            DayNumbers = days,
            Raw = raw
        };
    }

    // The leg of a member bib «100.2» / «101-2» / «101/2» (the same rule as IofXmlParser.SplitMemberBib).
    private static int? MemberBibLeg(string bib)
    {
        var parts = bib.Split(['-', '.', '/'], StringSplitOptions.TrimEntries);
        return parts.Length == 2 && PositiveInt(parts[0]) is not null ? PositiveInt(parts[1]) : null;
    }

    // The days of the Start elements' raceNumber attributes (a Start without one says nothing).
    private static IReadOnlyList<int> StartDays(IEnumerable<XElement> starts) =>
        Days(starts.Select(s => Attribute(s, "raceNumber") ?? string.Empty));

    // The clock of the dateTime in its own offset (the export writes «day + clock @ the day's offset»).
    private static TimeSpan? StartTime(XElement? start) =>
        DateTimeOffset.TryParse(Value(start, "StartTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.TimeOfDay
            : null;

    private static IReadOnlyList<int> Days(IEnumerable<string> values) =>
        values.Select(PositiveInt).OfType<int>().Distinct().Order().ToList();

    private static int? PositiveInt(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value.Trim();

    // «NationalRegion» is the region, any other type the club.
    private static (string Club, string Region) Organisation(XElement? organisation)
    {
        var name = Value(organisation, "Name");
        return organisation is not null && string.Equals(Attribute(organisation, "type"), "NationalRegion", StringComparison.OrdinalIgnoreCase)
            ? (string.Empty, name)
            : (name, string.Empty);
    }

    // Person/Id type="FSOU"; an untyped Id counts only when it is the person's only Id.
    private static string FsouCode(XElement? person)
    {
        var ids = Children(person, "Id").ToList();
        var fsou = ids.FirstOrDefault(i => string.Equals(Attribute(i, "type"), "FSOU", StringComparison.OrdinalIgnoreCase));
        if (fsou is null && ids.Count == 1 && string.IsNullOrEmpty(Attribute(ids[0], "type")))
            fsou = ids[0];
        return fsou?.Value.Trim() ?? string.Empty;
    }

    // «Прізвище Ім'я», the roster's convention.
    private static string FullName(XElement? person)
    {
        var name = Child(person, "Name");
        return string.Join(' ', new[] { Value(name, "Family"), Value(name, "Given") }.Where(s => s.Length > 0));
    }

    // xsd:date may carry an offset («2005-03-14+05:00»): only its first 10 characters are the date.
    private static DateTimeOffset? BirthDate(XElement? person)
    {
        var text = Value(person, "BirthDate");
        return text.Length >= 10
               && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
    }

    // Several cards (ControlCard/@punchingSystem): the SportIdent one, else one without a system, else the first;
    // "0" is none.
    private static string Chip(XElement? entry)
    {
        var cards = Children(entry, "ControlCard").ToList();
        var card = cards.FirstOrDefault(c => Attribute(c, "punchingSystem")?.ToUpperInvariant() is "SI" or "SPORTIDENT")
                   ?? cards.FirstOrDefault(c => string.IsNullOrEmpty(Attribute(c, "punchingSystem")))
                   ?? cards.FirstOrDefault();
        var chip = card?.Value.Trim() ?? string.Empty;
        return chip is "0" ? string.Empty : chip;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? [];

    private static XElement? Child(XElement? parent, string localName) => Children(parent, localName).FirstOrDefault();

    private static string Value(XElement? parent, string localName) => (Child(parent, localName)?.Value ?? string.Empty).Trim();
}
