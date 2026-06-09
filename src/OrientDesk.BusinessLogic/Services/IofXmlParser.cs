using System.Globalization;
using System.Xml.Linq;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

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

        return new IofCourseData
        {
            Version = version,
            MapScale = ReadScale(data),
            Controls = isV3 ? ReadControlsV3(data) : ReadControlsV2(data),
            Courses = isV3 ? ReadCoursesV3(data) : ReadCoursesV2(data)
        };
    }

    // --- Version detection ---------------------------------------------------

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

    // --- 2.0.3 ---------------------------------------------------------------

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
            controls.Add(new IofControl
            {
                Code = code,
                Type = type.Value,
                Latitude = lat,
                Longitude = lon
            });
        }

        return controls;
    }

    private static IReadOnlyList<IofCourse> ReadCoursesV2(XElement data)
    {
        var courses = new List<IofCourse>();

        foreach (var course in data.Elements().Where(e => LocalNameIs(e, "Course")))
        {
            var name = Trim(ChildValue(course, "CourseName"));
            // 2.0.3 nests the running order inside a <CourseVariation>; fall back to the course itself.
            var variation = Child(course, "CourseVariation") ?? course;

            var codes = new List<string>();
            AddCode(codes, Trim(ChildValue(variation, "StartPointCode")));
            foreach (var cc in variation.Elements().Where(e => LocalNameIs(e, "CourseControl")))
                AddCode(codes, Trim(ChildValue(cc, "ControlCode")));
            AddCode(codes, Trim(ChildValue(variation, "FinishPointCode")));

            courses.Add(new IofCourse
            {
                Name = name,
                Length = ParseInt(ChildValue(variation, "CourseLength")),
                Climb = ParseInt(ChildValue(variation, "CourseClimb")),
                ControlCodes = codes
            });
        }

        return courses;
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

    // --- 3.0 -----------------------------------------------------------------

    private static IReadOnlyList<IofControl> ReadControlsV3(XElement data)
    {
        var controls = new List<IofControl>();

        foreach (var element in data.Elements().Where(e => LocalNameIs(e, "Control")))
        {
            var code = Trim(ChildValue(element, "Id"));
            if (string.IsNullOrEmpty(code))
                continue;

            var position = Child(element, "Position");
            var lat = ParseDouble(position?.Attribute("lat")?.Value);
            var lon = ParseDouble(position?.Attribute("lng")?.Value);

            controls.Add(new IofControl
            {
                Code = code,
                Type = MapV3Type(element.Attribute("type")?.Value),
                Latitude = lat,
                Longitude = lon
            });
        }

        return controls;
    }

    private static IReadOnlyList<IofCourse> ReadCoursesV3(XElement data)
    {
        var courses = new List<IofCourse>();

        foreach (var course in data.Elements().Where(e => LocalNameIs(e, "Course")))
        {
            var codes = new List<string>();
            foreach (var cc in course.Elements().Where(e => LocalNameIs(e, "CourseControl")))
                AddCode(codes, Trim(ChildValue(cc, "Control")));

            courses.Add(new IofCourse
            {
                Name = Trim(ChildValue(course, "Name")),
                Length = ParseInt(ChildValue(course, "Length")),
                Climb = ParseInt(ChildValue(course, "Climb")),
                ControlCodes = codes
            });
        }

        return courses;
    }

    private static ControlPointType MapV3Type(string? type) => type switch
    {
        "Start" => ControlPointType.Start,
        "Finish" => ControlPointType.Finish,
        _ => ControlPointType.Regular
    };

    // --- Shared helpers ------------------------------------------------------

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

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
}
