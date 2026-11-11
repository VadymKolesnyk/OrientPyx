using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

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

/// <summary>A course (ordered sequence of control codes) parsed from an IOF file. «Розділення груп» rebuilds one per
/// group it splits the course into, carrying every property over.</summary>
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

    /// <summary>
    /// Distinct scatter («розсіювання») variants of this course, when the file defined more than one valid
    /// running order for the same course/family (2.0.3 multiple &lt;CourseVariation&gt;, 3.0 &lt;Course&gt;s
    /// sharing a &lt;CourseFamily&gt;). Empty for an ordinary single-order course. When populated the course's
    /// own <see cref="ControlCodes"/>/<see cref="Length"/>/<see cref="Climb"/> mirror the <b>first</b> variant
    /// (representative for display/distance); the importer detects a scatter course by <c>Variants.Count &gt; 1</c>.
    /// </summary>
    public IReadOnlyList<IofCourseVariant> Variants { get; init; } = [];

    /// <summary>
    /// Every variation the file defines for this course, in file order, each under its own code — identical running
    /// orders are <b>not</b> merged (an OCAD relay names «1AAAAA» and «4AAAAA» apart even when they run the same
    /// controls, and its team combinations refer to both). Empty when the file has fewer than two variations. A relay
    /// import stores these; a scatter import stores <see cref="Variants"/> unless the file assigns them to numbers.
    /// </summary>
    public IReadOnlyList<IofCourseVariant> AllVariants { get; init; } = [];

    /// <summary>
    /// The variation of each number and leg, as the course-setting program handed them out (2.0.3
    /// &lt;TeamCombination&gt;, 3.0 &lt;TeamCourseAssignment&gt; / &lt;PersonCourseAssignment&gt;). The codes are
    /// <see cref="AllVariants"/> codes. Empty when the file assigns nothing.
    /// </summary>
    public IReadOnlyList<IofTeamCombination> TeamCombinations { get; init; } = [];

    /// <summary>How the import treats the variations — chosen per course in «Розділення груп»; scatter by default.</summary>
    public CourseVariationUse VariationUse { get; init; } = CourseVariationUse.Scatter;

    /// <summary>True when the file gives this course several variations — the import then asks how to use them.</summary>
    public bool HasVariations => AllVariants.Count > 1;

    /// <summary>The most legs a team combination of this course has (0 when there are none).</summary>
    public int CombinationLegCount => TeamCombinations.Count == 0 ? 0 : TeamCombinations.Max(t => t.Legs.Count == 0 ? 0 : t.Legs.Max(l => l.Leg));
}

/// <summary>One valid running order of a scatter course parsed from an IOF file: a display <see cref="Code"/>
/// (from the file's variant name, or A/B/… assigned by order) and the ordered control codes (start/finish
/// included when present, exactly like <see cref="IofCourse.ControlCodes"/>).</summary>
public sealed record IofCourseVariant(string Code, IReadOnlyList<string> ControlCodes, int? Length, int? Climb);

/// <summary>The variations of one number («StartNo» of a team, or a runner's bib): leg → variation code.</summary>
public sealed record IofTeamCombination(int Number, IReadOnlyList<IofLegVariant> Legs);

/// <summary>The variation <see cref="Code"/> of one leg of a <see cref="IofTeamCombination"/>.</summary>
public sealed record IofLegVariant(int Leg, string Code);

/// <summary>How the import uses a course's variations («Розділення груп», per course).</summary>
public enum CourseVariationUse
{
    /// <summary>«Розсіювання»: the variants are running orders, each runner's detected from the read-out.</summary>
    Scatter,

    /// <summary>«Естафета», варіант за номером етапу: leg number «101.2» runs the variation assigned to it.</summary>
    RelayByBib,

    /// <summary>«Естафета», набір на команду: the team's variations are shared out over its legs in any order.</summary>
    RelayTeamSet
}
