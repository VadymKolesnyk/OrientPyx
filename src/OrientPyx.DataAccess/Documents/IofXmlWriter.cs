using System.Globalization;
using System.Text;
using System.Xml;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.DataAccess.Documents;

/// <summary>
/// Default <see cref="IIofXmlWriter"/>: writes the IOF 3.0 tree element by element in the order of <c>IOF.xsd</c>
/// (checked by the schema test), the standard's namespace as the default one. UTF-8 without a BOM, two-space indent and
/// "\n" line ends, so the bytes are the same on every platform (the golden files compare them). Dates are
/// <c>yyyy-MM-dd</c>, date-times <c>yyyy-MM-ddTHH:mm:sszzz</c>, numbers invariant, times whole seconds.
/// </summary>
public sealed class IofXmlWriter : IIofXmlWriter
{
    private const string Ns = IofDataStandard.Namespace;

    public byte[] Write(IofResultList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return Document(w =>
        {
            w.WriteStartElement("ResultList", Ns);
            Header(w, list.Header);
            w.WriteAttributeString("status", list.Status.ToString());
            Event(w, list.Event);
            foreach (var cls in list.Classes)
                ClassResult(w, cls);
            w.WriteEndElement();
        });
    }

    public byte[] Write(IofStartList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return Document(w =>
        {
            w.WriteStartElement("StartList", Ns);
            Header(w, list.Header);
            Event(w, list.Event);
            foreach (var cls in list.Classes)
                ClassStart(w, cls);
            w.WriteEndElement();
        });
    }

    private static byte[] Document(Action<XmlWriter> body)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument();
            body(w);
            w.WriteEndDocument();
        }
        return stream.ToArray();
    }

    // BaseMessageElement: iofVersion (required), createTime, creator.
    private static void Header(XmlWriter w, IofDocumentHeader header)
    {
        w.WriteAttributeString("iofVersion", IofDataStandard.Version);
        w.WriteAttributeString("createTime", DateTime(header.CreateTime));
        if (header.Creator.Trim().Length > 0)
            w.WriteAttributeString("creator", header.Creator.Trim());
    }

    // Event: Id?, Name, StartTime?, EndTime?, …, Organiser*, Official*, Class*, Race*.
    private static void Event(XmlWriter w, IofEvent ev)
    {
        w.WriteStartElement("Event", Ns);
        Text(w, "Name", ev.Name);
        w.WriteStartElement("StartTime", Ns);
        Text(w, "Date", Date(ev.StartDate));
        w.WriteEndElement();
        if (ev.EndDate is { } end)
        {
            w.WriteStartElement("EndTime", Ns);
            Text(w, "Date", Date(end));
            w.WriteEndElement();
        }
        if (ev.Organiser.Trim().Length > 0)
        {
            w.WriteStartElement("Organiser", Ns);
            Text(w, "Name", ev.Organiser.Trim());
            w.WriteEndElement();
        }
        foreach (var race in ev.Races)
        {
            w.WriteStartElement("Race", Ns);
            Text(w, "RaceNumber", Int(race.Number));
            Text(w, "Name", race.Name);
            w.WriteStartElement("StartTime", Ns);
            Text(w, "Date", Date(race.Date));
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // ClassResult: Class, Course*, PersonResult*, TeamResult*.
    private static void ClassResult(XmlWriter w, IofClassResult cls)
    {
        w.WriteStartElement("ClassResult", Ns);
        Class(w, cls.ClassName);
        foreach (var course in cls.Courses)
            Course(w, course);
        foreach (var person in cls.Persons)
        {
            // PersonResult: EntryId?, Person, Organisation?, Result*.
            w.WriteStartElement("PersonResult", Ns);
            Person(w, person.Person);
            Organisation(w, person.Organisation);
            foreach (var result in person.Results)
                RaceResult(w, result, member: false);
            w.WriteEndElement();
        }
        foreach (var team in cls.Teams)
        {
            // TeamResult: EntryId?, Name, Organisation*, BibNumber?, TeamMemberResult*.
            w.WriteStartElement("TeamResult", Ns);
            Text(w, "Name", team.Name);
            Organisation(w, team.Organisation);
            OptionalText(w, "BibNumber", team.Bib);
            foreach (var member in team.Members)
            {
                // TeamMemberResult: EntryId?, Person?, Organisation?, Result*.
                w.WriteStartElement("TeamMemberResult", Ns);
                Person(w, member.Person);
                Organisation(w, member.Organisation);
                RaceResult(w, member.Result, member: true);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // PersonRaceResult: BibNumber, StartTime, FinishTime, Time, TimeBehind, Position, Status, Score*, OverallResult,
    // Course, SplitTime*, ControlAnswer*, Route, ControlCard*. TeamMemberRaceResult leads with Leg, LegOrder.
    private static void RaceResult(XmlWriter w, IofRaceResult r, bool member)
    {
        w.WriteStartElement("Result", Ns);
        if (r.RaceNumber is { } race)
            w.WriteAttributeString("raceNumber", Int(race));
        if (member)
        {
            if (r.Leg is { } leg)
                Text(w, "Leg", Int(leg));
            if (r.LegOrder is { } order)
                Text(w, "LegOrder", Int(order));
        }
        OptionalText(w, "BibNumber", r.Bib);
        if (r.Start is { } start)
            Text(w, "StartTime", DateTime(start));
        if (r.Finish is { } finish)
            Text(w, "FinishTime", DateTime(finish));
        if (r.TimeSeconds is { } time)
            Text(w, "Time", Int(time));
        // A team member's TimeBehind / Position need a @type (Leg / Course) — the export writes neither.
        if (!member && r.TimeBehindSeconds is { } behind)
            Text(w, "TimeBehind", Int(behind));
        if (!member && r.Position is { } position)
            Text(w, "Position", Int(position));
        Text(w, "Status", r.Status.ToString());
        Scores(w, r.Scores);
        if (r.Overall is { } overall)
        {
            // OverallResult: Time, TimeBehind, Position, Status, Score*.
            w.WriteStartElement("OverallResult", Ns);
            if (overall.TimeSeconds is { } ot)
                Text(w, "Time", Int(ot));
            if (overall.TimeBehindSeconds is { } ob)
                Text(w, "TimeBehind", Int(ob));
            if (overall.Position is { } op)
                Text(w, "Position", Int(op));
            Text(w, "Status", overall.Status.ToString());
            Scores(w, overall.Scores);
            w.WriteEndElement();
        }
        if (r.Course is { } course)
            Course(w, course);
        foreach (var split in r.Splits)
        {
            // SplitTime: ControlCode, Time; @status (default OK).
            w.WriteStartElement("SplitTime", Ns);
            if (split.Status != IofSplitStatus.OK)
                w.WriteAttributeString("status", split.Status.ToString());
            Text(w, "ControlCode", split.Code);
            if (split.TimeSeconds is { } st)
                Text(w, "Time", Int(st));
            w.WriteEndElement();
        }
        OptionalText(w, "ControlCard", r.Chip);
        w.WriteEndElement();
    }

    // ClassStart: Class, Course*, StartName*, PersonStart*, TeamStart*.
    private static void ClassStart(XmlWriter w, IofClassStart cls)
    {
        w.WriteStartElement("ClassStart", Ns);
        Class(w, cls.ClassName);
        foreach (var course in cls.Courses)
            Course(w, course);
        foreach (var person in cls.Persons)
        {
            // PersonStart: EntryId?, Person?, Organisation?, Start+.
            w.WriteStartElement("PersonStart", Ns);
            Person(w, person.Person);
            Organisation(w, person.Organisation);
            foreach (var start in person.Starts)
                RaceStart(w, start, member: false);
            w.WriteEndElement();
        }
        foreach (var team in cls.Teams)
        {
            // TeamStart: EntryId?, Name?, Organisation*, BibNumber?, TeamMemberStart*.
            w.WriteStartElement("TeamStart", Ns);
            if (team.Name is not null)
                Text(w, "Name", team.Name);
            Organisation(w, team.Organisation);
            OptionalText(w, "BibNumber", team.Bib);
            foreach (var member in team.Members)
            {
                // TeamMemberStart: EntryId?, Person?, Organisation?, Start+.
                w.WriteStartElement("TeamMemberStart", Ns);
                Person(w, member.Person);
                Organisation(w, member.Organisation);
                RaceStart(w, member.Start, member: true);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    }

    // PersonRaceStart: BibNumber, StartTime, Course, ControlCard*; TeamMemberRaceStart leads with Leg, LegOrder.
    private static void RaceStart(XmlWriter w, IofRaceStart s, bool member)
    {
        w.WriteStartElement("Start", Ns);
        if (s.RaceNumber is { } race)
            w.WriteAttributeString("raceNumber", Int(race));
        if (member)
        {
            if (s.Leg is { } leg)
                Text(w, "Leg", Int(leg));
            if (s.LegOrder is { } order)
                Text(w, "LegOrder", Int(order));
        }
        OptionalText(w, "BibNumber", s.Bib);
        if (s.Start is { } start)
            Text(w, "StartTime", DateTime(start));
        if (s.Course is { } course)
            Course(w, course);
        OptionalText(w, "ControlCard", s.Chip);
        w.WriteEndElement();
    }

    private static void Class(XmlWriter w, string name)
    {
        w.WriteStartElement("Class", Ns);
        Text(w, "Name", name);
        w.WriteEndElement();
    }

    // SimpleCourse: Id, Name, CourseFamily, Length, Climb, NumberOfControls (+ @raceNumber on a class's course).
    private static void Course(XmlWriter w, IofSimpleCourse course)
    {
        w.WriteStartElement("Course", Ns);
        if (course.RaceNumber is { } race)
            w.WriteAttributeString("raceNumber", Int(race));
        OptionalText(w, "Name", course.Name);
        OptionalText(w, "CourseFamily", course.Family);
        if (course.LengthMetres is { } length)
            Text(w, "Length", Int(length));
        if (course.NumberOfControls is { } controls)
            Text(w, "NumberOfControls", Int(controls));
        w.WriteEndElement();
    }

    // Person: Id*, Name{Family, Given}, BirthDate.
    private static void Person(XmlWriter w, IofPerson person)
    {
        w.WriteStartElement("Person", Ns);
        if (person.FsouId is { Length: > 0 } id)
        {
            w.WriteStartElement("Id", Ns);
            w.WriteAttributeString("type", "FSOU");
            w.WriteString(id);
            w.WriteEndElement();
        }
        w.WriteStartElement("Name", Ns);
        Text(w, "Family", person.Family);
        Text(w, "Given", person.Given);
        w.WriteEndElement();
        if (person.BirthDate is { } birth)
            Text(w, "BirthDate", Date(birth));
        w.WriteEndElement();
    }

    private static void Organisation(XmlWriter w, IofOrganisation? organisation)
    {
        if (organisation is null)
            return;
        w.WriteStartElement("Organisation", Ns);
        w.WriteAttributeString("type", organisation.Type.ToString());
        Text(w, "Name", organisation.Name);
        w.WriteEndElement();
    }

    private static void Scores(XmlWriter w, IReadOnlyList<IofScore> scores)
    {
        foreach (var score in scores)
        {
            w.WriteStartElement("Score", Ns);
            w.WriteAttributeString("type", score.Type);
            w.WriteString(XmlConvert.ToString(score.Value));
            w.WriteEndElement();
        }
    }

    private static void Text(XmlWriter w, string name, string value) => w.WriteElementString(name, Ns, value);

    private static void OptionalText(XmlWriter w, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Text(w, name, value.Trim());
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DateTime(DateTimeOffset time) =>
        time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
