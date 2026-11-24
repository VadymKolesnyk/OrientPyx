namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The IOF Data Standard 3.0 constants the export writes (the readers match elements by local name and need none).
/// </summary>
public static class IofDataStandard
{
    /// <summary>The XML namespace of IOF Data Standard 3.0.</summary>
    public const string Namespace = "http://www.orienteering.org/datastandard/3.0";

    /// <summary>The <c>iofVersion</c> attribute every 3.0 message carries.</summary>
    public const string Version = "3.0";
}
