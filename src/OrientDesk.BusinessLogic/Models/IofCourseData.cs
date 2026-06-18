using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Layer-neutral result of parsing an IOF interchange XML file (an OCAD/Condes course export).
/// Produced by <see cref="Interfaces.IIofXmlParser"/> and consumed by importers. Holds the
/// controls and courses found in the file, independent of any database entity or UI type so the
/// same parse output can feed both the control-point import and (later) the course/group import.
/// </summary>
public sealed class IofCourseData
{
    /// <summary>The detected IOF data-standard version, e.g. "2.0.3" or "3.0".</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Map scale (e.g. 4000 for 1:4000) when the file states one; otherwise null.</summary>
    public int? MapScale { get; init; }

    /// <summary>All control points (incl. start/finish) found in the file, in file order.</summary>
    public IReadOnlyList<IofControl> Controls { get; init; } = [];

    /// <summary>All courses found in the file, in file order. Empty for control-only files.</summary>
    public IReadOnlyList<IofCourse> Courses { get; init; } = [];
}

/// <summary>A single control point parsed from an IOF file.</summary>
public sealed class IofControl
{
    /// <summary>Control code as written in the file, e.g. "31", "S1", "F". Trimmed.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Kind of point inferred from the file (start/finish/regular).</summary>
    public ControlPointType Type { get; init; } = ControlPointType.Regular;

    /// <summary>WGS-84 latitude, when the file carried geographic coordinates; otherwise null.</summary>
    public double? Latitude { get; init; }

    /// <summary>WGS-84 longitude, when the file carried geographic coordinates; otherwise null.</summary>
    public double? Longitude { get; init; }

    /// <summary>
    /// Paper map X position in millimetres (<c>&lt;MapPosition&gt;</c>), when present; otherwise null.
    /// Combined with <see cref="IofCourseData.MapScale"/> this gives true ground distance on the map
    /// plane — the distance orienteering software prints — and is preferred over geographic
    /// coordinates, which the Web Mercator export distorts by 1/cos(latitude).
    /// </summary>
    public double? MapX { get; init; }

    /// <summary>Paper map Y position in millimetres (<c>&lt;MapPosition&gt;</c>), when present; otherwise null.</summary>
    public double? MapY { get; init; }
}

/// <summary>A course (ordered sequence of control codes) parsed from an IOF file.</summary>
public sealed class IofCourse
{
    /// <summary>Course name, e.g. "ЧА", "OPEN". Trimmed.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Total course length in metres when stated; otherwise null.</summary>
    public int? Length { get; init; }

    /// <summary>Total climb in metres when stated; otherwise null.</summary>
    public int? Climb { get; init; }

    /// <summary>Control codes in running order (start and finish included when present).</summary>
    public IReadOnlyList<string> ControlCodes { get; init; } = [];
}
