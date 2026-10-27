using System.Globalization;
using System.Xml.Linq;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Reads an IOF data-standard 3.0 <c>&lt;EntryList&gt;</c> (entries exported from OEvent/OCAD/Eventor) into the same
/// <see cref="UofParticipantData"/> the participant import consumes. Relay entries (<c>&lt;TeamEntry&gt;</c>) give
/// one participant per <c>&lt;TeamEntryPerson&gt;</c> carrying the team number (the team's <c>BibNumber</c>), the team
/// name and the person's <c>&lt;Leg&gt;</c>; individual entries (<c>&lt;PersonEntry&gt;</c>) give a plain participant.
/// The class is the group. The file carries no day info, so every entry runs on all days (the import's rule); the
/// relay team/leg is applied on the relay days only.
/// </summary>
internal static class IofEntryListReader
{
    public static bool IsEntryList(XElement root) => root.Name.LocalName == "EntryList";

    public static UofParticipantData Read(XElement root)
    {
        var participants = new List<UofParticipant>();

        // Team numbers: the team's bib, else its numeric id, else the next number after the highest one seen.
        var teams = Children(root, "TeamEntry").ToList();
        var used = new HashSet<int>();
        var numbers = new int?[teams.Count];
        for (var i = 0; i < teams.Count; i++)
            if (TeamNumberOf(teams[i]) is { } n && used.Add(n))
                numbers[i] = n;
        var next = used.Count == 0 ? 1 : used.Max() + 1;

        for (var i = 0; i < teams.Count; i++)
        {
            var team = teams[i];
            var number = numbers[i] ?? next++;
            var teamName = Value(team, "Name");
            var teamClub = Value(Child(team, "Organisation"), "Name");
            var group = Value(Child(team, "Class"), "Name");

            foreach (var member in Children(team, "TeamEntryPerson"))
            {
                var person = Child(member, "Person");
                var name = FullName(person);
                if (name.Length == 0)
                    continue;
                var club = Value(Child(member, "Organisation"), "Name");
                participants.Add(new UofParticipant
                {
                    FullName = name,
                    BirthDate = BirthDate(person),
                    Club = club.Length > 0 ? club : teamClub,
                    Group = group,
                    Chip = Chip(member),
                    Team = teamName,
                    RelayTeamNumber = number,
                    RelayTeamName = teamName,
                    RelayLeg = int.TryParse(Value(member, "Leg"), NumberStyles.None, CultureInfo.InvariantCulture, out var leg)
                               && leg > 0 ? leg : null
                });
            }
        }

        foreach (var entry in Children(root, "PersonEntry"))
        {
            var person = Child(entry, "Person");
            var name = FullName(person);
            if (name.Length == 0)
                continue;
            participants.Add(new UofParticipant
            {
                FullName = name,
                BirthDate = BirthDate(person),
                Club = Value(Child(entry, "Organisation"), "Name"),
                Group = Value(Child(entry, "Class"), "Name"),
                Chip = Chip(entry),
                Number = Value(entry, "BibNumber")
            });
        }

        return new UofParticipantData { Participants = participants };
    }

    private static int? TeamNumberOf(XElement team)
    {
        foreach (var raw in new[] { Value(team, "BibNumber"), Value(team, "Id") })
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                return n;
        return null;
    }

    // «Прізвище Ім'я», the roster's convention.
    private static string FullName(XElement? person)
    {
        var name = Child(person, "Name");
        return string.Join(' ', new[] { Value(name, "Family"), Value(name, "Given") }.Where(s => s.Length > 0));
    }

    private static DateTimeOffset? BirthDate(XElement? person) =>
        DateTime.TryParse(Value(person, "BirthDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d.Date, TimeSpan.Zero)
            : null;

    private static string Chip(XElement entry)
    {
        var chip = Value(entry, "ControlCard");
        return chip is "0" ? string.Empty : chip;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? [];

    private static XElement? Child(XElement? parent, string localName) => Children(parent, localName).FirstOrDefault();

    private static string Value(XElement? parent, string localName) => (Child(parent, localName)?.Value ?? string.Empty).Trim();
}
