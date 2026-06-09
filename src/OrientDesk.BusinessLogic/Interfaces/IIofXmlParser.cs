using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Parses IOF interchange XML (course data exported from OCAD/Condes) into the layer-neutral
/// <see cref="IofCourseData"/>. Supports both IOF data-standard <b>2.0.3</b> and <b>3.0</b>.
/// Pure parsing: no files are opened here and no entities are produced, so the same result can
/// feed the control-point import today and the course/group import later.
/// </summary>
public interface IIofXmlParser
{
    /// <summary>
    /// Parses an XML document supplied as text. Throws <see cref="IofXmlFormatException"/> when the
    /// content is not a recognised IOF CourseData document.
    /// </summary>
    IofCourseData Parse(string xml);
}

/// <summary>Raised when an XML document is not a recognisable IOF CourseData file.</summary>
public sealed class IofXmlFormatException : Exception
{
    public IofXmlFormatException(string message) : base(message) { }
}
