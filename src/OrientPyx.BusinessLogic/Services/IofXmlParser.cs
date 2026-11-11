using System.Globalization;
using System.Xml.Linq;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IIofXmlParser"/>. Reads both IOF data-standard 2.0.3 and 3.0 CourseData
/// documents into <see cref="IofCourseData"/>.
///
/// The two standards differ in shape, so element names are matched by local name (ignoring the
/// XML namespace that 3.0 declares and 2.0.3 omits):
/// <list type="bullet">
///   <item>2.0.3 — controls in &lt;Control&gt;/&lt;StartPoint&gt;/&lt;FinishPoint&gt; with a
///   &lt;ControlCode&gt; child; geographic position (when present) is a projected
///   &lt;ControlPosition&gt; in EPSG:3857 metres, converted to WGS-84 here.</item>
///   <item>3.0 — controls in &lt;Control&gt; with an &lt;Id&gt; child and a &lt;Position&gt;
///   carrying WGS-84 lng/lat attributes directly.</item>
/// </list>
/// </summary>
public sealed class IofXmlParser : IIofXmlParser
{
    public IofCourseData Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new IofXmlFormatException("The file is empty.");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            throw new IofXmlFormatException("The file is not valid XML: " + ex.Message);
        }

        var root = doc.Root;
        if (root is null || !LocalNameIs(root, "CourseData"))
            throw new IofXmlFormatException("The file is not an IOF CourseData document.");

        var version = DetectVersion(root);
        var isV3 = version.StartsWith("3.", StringComparison.Ordinal);

        // 3.0 wraps the data in <RaceCourseData>; 2.0.3 keeps everything directly under the root.
        var data = isV3
            ? Child(root, "RaceCourseData") ?? root
            : root;

        var controls = isV3 ? ReadControlsV3(data) : ReadControlsV2(data);

        // Start/finish codes, so scatter de-duplication compares only the running order (two variants that
        // differ solely by whether they list the finish box are the same order, not distinct petals).
        var startFinishCodes = new HashSet<string>(
            controls
                .Where(c => c.Type is ControlPointType.Start or ControlPointType.Finish)
                .Select(c => c.Code),
            StringComparer.OrdinalIgnoreCase);

        return new IofCourseData
        {
            Version = version,
            MapScale = ReadScale(data),
            Controls = controls,
            Courses = isV3 ? ReadCoursesV3(data, startFinishCodes) : ReadCoursesV2(data, startFinishCodes)
        };
    }

    // --- Version detection

    private static string DetectVersion(XElement root)
    {
        // 3.0 carries the version as an attribute on the root.
        var attr = root.Attribute("iofVersion")?.Value;
        if (!string.IsNullOrWhiteSpace(attr))
            return attr.Trim();

        // 2.0.3 carries it as a child element: <IOFVersion version="2.0.3" />.
        var versionElement = Child(root, "IOFVersion");
        var elementVersion = versionElement?.Attribute("version")?.Value;
        if (!string.IsNullOrWhiteSpace(elementVersion))
            return elementVersion.Trim();

        // No explicit marker: infer from shape. A <RaceCourseData> child means 3.0.
        return Child(root, "RaceCourseData") is not null ? "3.0" : "2.0.3";
    }

    // --- 2.0.3

    private static IReadOnlyList<IofControl> ReadControlsV2(XElement data)
    {
        var controls = new List<IofControl>();

        foreach (var element in data.Elements())
        {
            var type = LocalName(element) switch
            {
                "StartPoint" => (ControlPointType?)ControlPointType.Start,
                "FinishPoint" => ControlPointType.Finish,
                "Control" => ControlPointType.Regular,
                _ => null
            };
            if (type is null)
                continue;

            var code = Trim(ChildValue(element, "ControlCode")
                            ?? ChildValue(element, "StartPointCode")
                            ?? ChildValue(element, "FinishPointCode"));
            if (string.IsNullOrEmpty(code))
                continue;

            var (lat, lon) = ReadProjectedPosition(element);
            var (mapX, mapY) = ReadMapPosition(element);
            controls.Add(new IofControl
            {
                Code = code,
                Type = type.Value,
                Latitude = lat,
                Longitude = lon,
                MapX = mapX,
                MapY = mapY
            });
        }

        return controls;
    }

    private static IReadOnlyList<IofCourse> ReadCoursesV2(XElement data, IReadOnlySet<string> startFinishCodes)
    {
        var courses = new List<IofCourse>();
        var combinations = ReadTeamCombinationsV2(data);

        foreach (var course in data.Elements().Where(e => LocalNameIs(e, "Course")))
        {
            var name = Trim(ChildValue(course, "CourseName"));

            // 2.0.3 nests each running order inside a <CourseVariation>; a scatter or relay course carries several
            // (with an optional <Name> per variation). Read them all and fall back to the course itself when there
            // is no <CourseVariation> at all.
            var variations = course.Elements().Where(e => LocalNameIs(e, "CourseVariation")).ToList();
            var sources = variations.Count > 0 ? variations : [course];

            var raw = new List<ParsedVariation>();
            foreach (var variation in sources)
            {
                var codes = new List<string>();
                AddCode(codes, Trim(ChildValue(variation, "StartPointCode")));
                foreach (var cc in variation.Elements().Where(e => LocalNameIs(e, "CourseControl")))
                    AddCode(codes, Trim(ChildValue(cc, "ControlCode")));
                AddCode(codes, Trim(ChildValue(variation, "FinishPointCode")));

                raw.Add(new ParsedVariation(
                    Trim(ChildValue(variation, "Name")),
                    Trim(ChildValue(variation, "CourseVariationId")),
                    codes,
                    ParseInt(ChildValue(variation, "CourseLength")),
                    ParseInt(ChildValue(variation, "CourseClimb"))));
            }

            var courseId = Trim(ChildValue(course, "CourseId"));
            var picks = courseId.Length > 0 && combinations.TryGetValue(courseId, out var list) ? list : [];
            courses.Add(BuildCourse(name, raw, startFinishCodes, picks));
        }

        return courses;
    }

    /// <summary>
    /// OCAD's 2.0.3 relay export lists, after the courses, one &lt;TeamCombination&gt; per team: its &lt;StartNo&gt;,
    /// the &lt;CourseId&gt; and per &lt;Leg&gt; the &lt;LegNo&gt; with the &lt;CourseVariationId&gt; (and &lt;Name&gt;) of
    /// the variation that leg runs. Grouped by course id; entries without a number or a leg are skipped.
    /// </summary>
    private static Dictionary<string, List<VariationPick>> ReadTeamCombinationsV2(XElement data)
    {
        var result = new Dictionary<string, List<VariationPick>>(StringComparer.Ordinal);
        foreach (var combination in data.Elements().Where(e => LocalNameIs(e, "TeamCombination")))
        {
            var number = LeadingNumber(ChildValue(combination, "StartNo"));
            var courseId = Trim(ChildValue(combination, "CourseId"));
            if (number is null || courseId.Length == 0)
                continue;
            foreach (var leg in combination.Elements().Where(e => LocalNameIs(e, "Leg")))
            {
                if (ParseInt(ChildValue(leg, "LegNo")) is not { } legNo || legNo < 1)
                    continue;
                if (!result.TryGetValue(courseId, out var list))
                    result[courseId] = list = [];
                list.Add(new VariationPick(number.Value, legNo,
                    Trim(ChildValue(leg, "CourseVariationId")), Trim(ChildValue(leg, "Name"))));
            }
        }
        return result;
    }

    /// <summary>
    /// Reads a 2.0.3 &lt;ControlPosition&gt; (EPSG:3857 metres) and converts it to WGS-84.
    /// Returns (null, null) when the element has no projected position — older exports carry only
    /// &lt;MapPosition&gt;, which is paper millimetres and not geographic, so it is intentionally
    /// ignored here.
    /// </summary>
    private static (double? lat, double? lon) ReadProjectedPosition(XElement element)
    {
        var position = Child(element, "ControlPosition");
        if (position is null)
            return (null, null);

        var x = ParseDouble(position.Attribute("x")?.Value);
        var y = ParseDouble(position.Attribute("y")?.Value);
        if (x is null || y is null)
            return (null, null);

        return WebMercatorToWgs84(x.Value, y.Value);
    }

    /// <summary>
    /// Reads a <c>&lt;MapPosition&gt;</c> (paper millimetres on the printed map), shared by both
    /// standards. Returns (null, null) when absent. Combined with the map scale this gives the
    /// course distance orienteering software prints, undistorted by any geographic projection — and
    /// it is the only positional data some exports (e.g. Condes 3.0) carry at all.
    /// </summary>
    private static (double? x, double? y) ReadMapPosition(XElement element)
    {
        var position = Child(element, "MapPosition");
        if (position is null)
            return (null, null);

        return (ParseDouble(position.Attribute("x")?.Value), ParseDouble(position.Attribute("y")?.Value));
    }

    // --- 3.0

    private static IReadOnlyList<IofControl> ReadControlsV3(XElement data)
    {
        // OCAD's 3.0 export gives each <Control> definition only an <Id>, no `type` attribute, so
        // start/finish are not marked there. The kind is instead carried by each <CourseControl
        // type="Start|Finish|Control"> usage inside the courses. Build a code→type map from those
        // first and fall back to the (usually absent) `type` attribute on the definition.
        var typesFromCourses = ReadControlTypesFromCoursesV3(data);
        var controls = new List<IofControl>();

        foreach (var element in data.Elements().Where(e => LocalNameIs(e, "Control")))
        {
            var code = Trim(ChildValue(element, "Id"));
            if (string.IsNullOrEmpty(code))
                continue;

            var position = Child(element, "Position");
            var lat = ParseDouble(position?.Attribute("lat")?.Value);
            var lon = ParseDouble(position?.Attribute("lng")?.Value);
            var (mapX, mapY) = ReadMapPosition(element);

            var type = typesFromCourses.TryGetValue(code, out var courseType)
                ? courseType
                : MapV3Type(element.Attribute("type")?.Value);

            controls.Add(new IofControl
            {
                Code = code,
                Type = type,
                Latitude = lat,
                Longitude = lon,
                MapX = mapX,
                MapY = mapY
            });
        }

        return controls;
    }

    /// <summary>
    /// Scans every 3.0 &lt;Course&gt;/&lt;CourseControl type="..."&gt; and maps each control code to
    /// its kind. A Start/Finish marking wins over Control, so a code used both as a course start and
    /// (theoretically) a regular control is still reported as a start.
    /// </summary>
    private static Dictionary<string, ControlPointType> ReadControlTypesFromCoursesV3(XElement data)
    {
        var types = new Dictionary<string, ControlPointType>(StringComparer.Ordinal);

        foreach (var course in data.Elements().Where(e => LocalNameIs(e, "Course")))
        foreach (var cc in course.Elements().Where(e => LocalNameIs(e, "CourseControl")))
        {
            var code = Trim(ChildValue(cc, "Control"));
            if (string.IsNullOrEmpty(code))
                continue;

            var type = MapV3Type(cc.Attribute("type")?.Value);
            if (types.TryGetValue(code, out var existing) && existing != ControlPointType.Regular)
                continue; // keep a previously seen Start/Finish marking

            types[code] = type;
        }

        return types;
    }

    private static IReadOnlyList<IofCourse> ReadCoursesV3(XElement data, IReadOnlySet<string> startFinishCodes)
    {
        var courses = new List<IofCourse>();

        // 3.0 gives each variant its own <Course>, linking them by a shared <CourseFamily>. Group the courses
        // by family (in first-seen order) so a scatter family's variants stay together; a course with no family
        // is its own single-course group. The family name (not the per-variant "…_A" name) is the group name,
        // so it matches the participants' group, while each variant keeps its own suffix as its code.
        var families = new List<(string Key, string DisplayName, List<XElement> Courses)>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal); // family key → index in `families`

        foreach (var course in data.Elements().Where(e => LocalNameIs(e, "Course")))
        {
            var courseName = Trim(ChildValue(course, "Name"));
            var family = Trim(ChildValue(course, "CourseFamily"));

            if (family.Length == 0)
            {
                // Standalone course: its own group, keyed uniquely so it never merges with another.
                families.Add(($"\0course:{families.Count}", courseName, [course]));
                continue;
            }

            if (byKey.TryGetValue(family, out var idx))
                families[idx].Courses.Add(course);
            else
            {
                byKey[family] = families.Count;
                families.Add((family, family, [course]));
            }
        }

        // The relay / per-runner assignments, each to the family its course belongs to: the <CourseFamily> it names,
        // else the family holding a course of that name.
        var familyOfCourse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < families.Count; i++)
            foreach (var course in families[i].Courses)
                familyOfCourse.TryAdd(Trim(ChildValue(course, "Name")), i);
        var picksByFamily = new Dictionary<int, List<VariationPick>>();
        foreach (var (family, courseName, pick) in ReadCourseAssignmentsV3(data))
        {
            var index = family.Length > 0 && byKey.TryGetValue(family, out var f) ? f
                : familyOfCourse.TryGetValue(courseName, out var c) ? c
                : -1;
            if (index < 0)
                continue;
            if (!picksByFamily.TryGetValue(index, out var list))
                picksByFamily[index] = list = [];
            list.Add(pick);
        }

        for (var i = 0; i < families.Count; i++)
        {
            var (_, displayName, familyCourses) = families[i];
            var raw = new List<ParsedVariation>();
            foreach (var course in familyCourses)
            {
                var codes = new List<string>();
                foreach (var cc in course.Elements().Where(e => LocalNameIs(e, "CourseControl")))
                    AddCode(codes, Trim(ChildValue(cc, "Control")));

                // The variant code is the course-name suffix past the family name (e.g. "…_розсіювання_A" → "A");
                // BuildCourse falls back to A/B/… by order when the suffix is blank or duplicated. The full course
                // name is the key the assignments refer to it by.
                var courseName = Trim(ChildValue(course, "Name"));
                raw.Add(new ParsedVariation(
                    VariantSuffix(courseName, displayName),
                    courseName,
                    codes,
                    ParseInt(ChildValue(course, "Length")),
                    ParseInt(ChildValue(course, "Climb"))));
            }

            courses.Add(BuildCourse(displayName, raw, startFinishCodes, picksByFamily.GetValueOrDefault(i) ?? []));
        }

        return courses;
    }

    /// <summary>
    /// The 3.0 course assignments: a &lt;TeamCourseAssignment&gt; (team &lt;BibNumber&gt;, one
    /// &lt;TeamMemberCourseAssignment&gt; per leg with &lt;Leg&gt; — else the leg part of its own bib «101-2» — and the
    /// &lt;CourseName&gt; / &lt;CourseFamily&gt; it runs) and a &lt;PersonCourseAssignment&gt; (a runner's bib and
    /// course, as leg 1). The pick's key is the course name.
    /// </summary>
    private static IEnumerable<(string Family, string CourseName, VariationPick Pick)> ReadCourseAssignmentsV3(XElement data)
    {
        foreach (var team in data.Elements().Where(e => LocalNameIs(e, "TeamCourseAssignment")))
        {
            var teamNumber = LeadingNumber(ChildValue(team, "BibNumber"));
            foreach (var member in team.Elements().Where(e => LocalNameIs(e, "TeamMemberCourseAssignment")))
            {
                var courseName = Trim(ChildValue(member, "CourseName"));
                if (courseName.Length == 0)
                    continue;
                var number = teamNumber;
                var leg = ParseInt(ChildValue(member, "Leg"));
                if (SplitMemberBib(ChildValue(member, "BibNumber")) is { } bib)
                {
                    number ??= bib.Team;
                    leg ??= bib.Leg;
                }
                if (number is { } n && leg is { } l && l >= 1)
                    yield return (Trim(ChildValue(member, "CourseFamily")), courseName, new VariationPick(n, l, courseName, string.Empty));
            }
        }

        foreach (var person in data.Elements().Where(e => LocalNameIs(e, "PersonCourseAssignment")))
        {
            var courseName = Trim(ChildValue(person, "CourseName"));
            if (courseName.Length > 0 && LeadingNumber(ChildValue(person, "BibNumber")) is { } number)
                yield return (Trim(ChildValue(person, "CourseFamily")), courseName, new VariationPick(number, 1, courseName, string.Empty));
        }
    }

    // A team member's bib «101-2» / «101.2» / «101/2» → (101, 2); anything else → null.
    private static (int Team, int Leg)? SplitMemberBib(string? text)
    {
        var parts = Trim(text).Split(['-', '.', '/'], StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var team)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var leg)
               && team > 0 && leg > 0
            ? (team, leg)
            : null;
    }

    // The trailing part of a variant's course name past the family name and a separating '_' — e.g.
    // ("Ж18_естафета_розсіювання_A", "Ж18_естафета_розсіювання") → "A". Blank when the name doesn't extend the
    // family (then BuildCourse assigns A/B/… by order).
    private static string VariantSuffix(string courseName, string familyName)
    {
        if (familyName.Length > 0
            && courseName.Length > familyName.Length
            && courseName.StartsWith(familyName, StringComparison.OrdinalIgnoreCase))
        {
            var rest = courseName[familyName.Length..].TrimStart('_', ' ', '-');
            return rest;
        }
        return string.Empty;
    }

    // --- Course / variant assembly (shared by both standards)

    // A single running order read from the file, before de-duplication: its file-supplied name (may be blank), the
    // key the file's assignments refer to it by (2.0.3 variation id, 3.0 course name), its control codes in order,
    // and its stated length/climb.
    private readonly record struct ParsedVariation(string Name, string Key, IReadOnlyList<string> Codes, int? Length, int? Climb);

    // One assignment read from the file: number + leg → the variation with this key (or, failing that, this name).
    private readonly record struct VariationPick(int Number, int Leg, string Key, string Name);

    /// <summary>
    /// Turns the raw variations read for one course/family into an <see cref="IofCourse"/>.
    /// <list type="bullet">
    ///   <item><c>Variants</c> — the distinct running orders: variations with an <b>identical</b> control-code
    ///   sequence (trimmed, case-insensitive, start/finish excluded) are collapsed to the first, since only genuinely
    ///   distinct orders count as scatter variants. A single distinct order leaves it empty (an ordinary course).</item>
    ///   <item><c>AllVariants</c> — every variation, nothing collapsed (a relay's «1AAAAA» and «4AAAAA» stay apart even
    ///   when they run the same controls); empty when the file has fewer than two.</item>
    ///   <item><c>TeamCombinations</c> — the file's number/leg assignments, resolved to those codes.</item>
    /// </list>
    /// A variation is coded by its file name when present and unique, else A/B/C… by order — the distinct ones first,
    /// so their codes don't depend on the repeats. The course's own codes/length/climb mirror the first variation.
    /// </summary>
    private static IofCourse BuildCourse(
        string name, IReadOnlyList<ParsedVariation> variations, IReadOnlySet<string> startFinishCodes,
        IReadOnlyList<VariationPick> picks)
    {
        // Normalise each running order first: drop any start/finish box that isn't the very first or last
        // code (some exports scatter S/F markers mid-course), then collapse consecutive duplicate codes.
        // So "31 32 S1 F1 32 33" → "31 32 33". Variations left with no codes at all are dropped.
        variations = variations
            .Select(v => v with { Codes = CleanCourseCodes(v.Codes, startFinishCodes) })
            .Where(v => v.Codes.Count > 0)
            .ToList();
        if (variations.Count == 0)
            return new IofCourse { Name = name, ControlCodes = [] };

        // The distinct RUNNING sequences (start/finish markers excluded), first seen kept: two variations that differ
        // only by whether they list the start/finish box are the same running order, not distinct petals.
        var distinct = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variations.Count; i++)
        {
            var running = variations[i].Codes.Where(c => !startFinishCodes.Contains(c));
            if (seen.Add(string.Join(" ", running).ToUpperInvariant()))
                distinct.Add(i);
        }

        var first = variations[0];
        if (variations.Count == 1)
            return new IofCourse { Name = name, Length = first.Length, Climb = first.Climb, ControlCodes = first.Codes };

        // Codes: the file name when non-blank and not already used, else the next letter A, B, C… (skipping any
        // letter a file name already took). Distinct orders are coded first, then the repeats.
        var codes = new string[variations.Count];
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var letter = 0;
        string NextCode(ParsedVariation v)
        {
            if (v.Name.Length > 0 && usedCodes.Add(v.Name))
                return v.Name;
            string code;
            do { code = NextLetter(letter++); } while (!usedCodes.Add(code));
            return code;
        }
        foreach (var i in distinct)
            codes[i] = NextCode(variations[i]);
        for (var i = 0; i < variations.Count; i++)
            codes[i] ??= NextCode(variations[i]);

        IofCourseVariant Variant(int i) =>
            new(codes[i], variations[i].Codes, variations[i].Length, variations[i].Climb);

        return new IofCourse
        {
            Name = name,
            Length = first.Length,
            Climb = first.Climb,
            ControlCodes = first.Codes,
            Variants = distinct.Count > 1 ? distinct.Select(Variant).ToList() : [],
            AllVariants = Enumerable.Range(0, variations.Count).Select(Variant).ToList(),
            TeamCombinations = Combinations(picks, variations, codes)
        };
    }

    // The file's assignments as number → legs, each leg resolved to a variation code: by the variation's key (2.0.3 id,
    // 3.0 course name), else by its name; an unknown one keeps the name the file gave (so the import can report it).
    // The first assignment of a number's leg wins; numbers in file order, legs in leg order.
    private static IReadOnlyList<IofTeamCombination> Combinations(
        IReadOnlyList<VariationPick> picks, IReadOnlyList<ParsedVariation> variations, IReadOnlyList<string> codes)
    {
        if (picks.Count == 0)
            return [];
        var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byCode = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variations.Count; i++)
            if (variations[i].Key.Length > 0)
                byKey.TryAdd(variations[i].Key, codes[i]);

        var result = new List<IofTeamCombination>();
        foreach (var number in picks.GroupBy(p => p.Number))
        {
            var legs = new List<IofLegVariant>();
            foreach (var pick in number.GroupBy(p => p.Leg).OrderBy(g => g.Key).Select(g => g.First()))
            {
                var code = byKey.TryGetValue(pick.Key, out var c) ? c
                    : byCode.TryGetValue(pick.Name, out var named) ? named
                    : pick.Name;
                if (code.Length > 0)
                    legs.Add(new IofLegVariant(pick.Leg, code));
            }
            if (legs.Count > 0)
                result.Add(new IofTeamCombination(number.Key, legs));
        }
        return result;
    }

    /// <summary>
    /// Cleans one running order for storage: removes every start/finish code that is <b>not</b> the first or
    /// last code (interior S/F markers some exports emit between real controls), then collapses runs of the
    /// same code left behind. Example: <c>31 32 S1 F1 32 33</c> → <c>31 32 33</c>. A leading start / trailing
    /// finish box is preserved (downstream scoring filters those out by day start/finish code anyway).
    /// </summary>
    private static IReadOnlyList<string> CleanCourseCodes(
        IReadOnlyList<string> codes, IReadOnlySet<string> startFinishCodes)
    {
        var kept = new List<string>(codes.Count);
        for (var i = 0; i < codes.Count; i++)
        {
            var isEdge = i == 0 || i == codes.Count - 1;
            if (!isEdge && startFinishCodes.Contains(codes[i]))
                continue; // drop an interior start/finish marker

            // Collapse consecutive duplicates (they arise once the interior S/F between two equal codes is gone).
            if (kept.Count > 0 && string.Equals(kept[^1], codes[i], StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(codes[i]);
        }
        return kept;
    }

    // A, B, …, Z, AA, AB, … for the given 0-based index (fallback variant code when the file names none).
    private static string NextLetter(int index)
    {
        var sb = new System.Text.StringBuilder();
        index++;
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    private static ControlPointType MapV3Type(string? type) => type switch
    {
        "Start" => ControlPointType.Start,
        "Finish" => ControlPointType.Finish,
        _ => ControlPointType.Regular
    };

    // --- Shared helpers

    private static int? ReadScale(XElement data)
    {
        var map = Child(data, "Map");
        return ParseInt(map is null ? null : ChildValue(map, "Scale"));
    }

    private static void AddCode(List<string> codes, string code)
    {
        if (!string.IsNullOrEmpty(code))
            codes.Add(code);
    }

    /// <summary>
    /// Inverse spherical Web Mercator (EPSG:3857) → WGS-84 degrees. OCAD exports control
    /// positions in this projection, which is what the v3.0 lng/lat round-trips to as well.
    /// </summary>
    private static (double lat, double lon) WebMercatorToWgs84(double x, double y)
    {
        const double r = 6378137.0; // WGS-84 equatorial radius used by EPSG:3857
        var lon = (x / r) * (180.0 / Math.PI);
        var lat = (2.0 * Math.Atan(Math.Exp(y / r)) - Math.PI / 2.0) * (180.0 / Math.PI);
        return (lat, lon);
    }

    private static bool LocalNameIs(XElement element, string name) =>
        string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

    private static string LocalName(XElement element) => element.Name.LocalName;

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => LocalNameIs(e, localName));

    private static string? ChildValue(XElement parent, string localName) =>
        Child(parent, localName)?.Value;

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    // The leading digits of a number field («101», « 101 », «101A») as a positive number; null otherwise.
    private static int? LeadingNumber(string? text)
    {
        var digits = new string(Trim(text).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
}
