using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Reads relay leg-number courses (<see cref="Enums.RelayCourseMode.ByBib"/>, Р-41) from a file's text: an IOF 3.0
/// <c>CourseData</c> with <c>TeamCourseAssignment</c> elements (OCAD's relay export), or a delimited table — one
/// leg per line as «Номер етапу; Дистанція» («100.2;B») or «Номер команди; Етап; Дистанція» («100;2;B»). A header
/// line and lines that don't parse are skipped.
/// </summary>
internal static class LegCourseAssignmentReader
{
    public static IReadOnlyList<LegCourseEntry> Read(string content)
    {
        var text = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith('<') ? ReadIof(text) : ReadTable(text);
    }

    private static List<LegCourseEntry> ReadIof(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new FormatException(ex.Message, ex);
        }

        static string? Child(XElement e, string name) =>
            e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim();

        var result = new List<LegCourseEntry>();
        foreach (var team in doc.Descendants().Where(e => e.Name.LocalName == "TeamCourseAssignment"))
        {
            var teamNumber = LeadingNumber(Child(team, "BibNumber"));
            foreach (var member in team.Elements().Where(e => e.Name.LocalName == "TeamMemberCourseAssignment"))
            {
                var course = Child(member, "CourseName");
                if (string.IsNullOrWhiteSpace(course))
                    continue;
                // The leg is the <Leg> element, else the second part of the member bib («101-2» / «101.2»).
                var leg = int.TryParse(Child(member, "Leg"), NumberStyles.None, CultureInfo.InvariantCulture, out var l)
                    ? l
                    : (int?)null;
                var number = teamNumber;
                if (Child(member, "BibNumber") is { Length: > 0 } bib && SplitBib(bib) is { } parts)
                {
                    number ??= parts.Team;
                    leg ??= parts.Leg;
                }
                if (number is { } n && leg is { } lg)
                    result.Add(new LegCourseEntry(n, lg, course));
            }
        }
        return result;
    }

    private static List<LegCourseEntry> ReadTable(string text)
    {
        var result = new List<LegCourseEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var cells = line.Split([';', '\t', ','], StringSplitOptions.TrimEntries).Select(c => c.Trim('"')).ToArray();
            if (cells.Length >= 3
                && int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out var team)
                && int.TryParse(cells[1], NumberStyles.None, CultureInfo.InvariantCulture, out var leg)
                && cells[2].Length > 0)
                result.Add(new LegCourseEntry(team, leg, cells[2]));
            else if (cells.Length >= 2 && SplitBib(cells[0]) is { } bib && cells[1].Length > 0)
                result.Add(new LegCourseEntry(bib.Team, bib.Leg, cells[1]));
        }
        return result;
    }

    // «100.2» / «100-2» / «100/2» → (100, 2).
    private static (int Team, int Leg)? SplitBib(string bib)
    {
        if (RelayBib.TryParse(bib, out var team, out var leg) && leg is { } relayLeg)
            return (team, relayLeg);
        var parts = bib.Split(['-', '/'], StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var t)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var l)
               && t > 0 && l > 0
            ? (t, l)
            : null;
    }

    private static int? LeadingNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }
}
