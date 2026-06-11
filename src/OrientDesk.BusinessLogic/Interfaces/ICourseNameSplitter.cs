namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Splits a combined course name into its constituent group names. IOF course exports often glue
/// two single-letter group prefixes onto one number/suffix — e.g. "ЧЖ55" means the two groups
/// "Ч55" and "Ж55" run the same course. This expands such names so each group can be imported on
/// its own. Pure string logic, no UI or persistence; the splitter dialog and the import flow both
/// rely on it.
/// </summary>
public interface ICourseNameSplitter
{
    /// <summary>
    /// Splits <paramref name="courseName"/> into individual group names (e.g. "ЧЖ55" →
    /// ["Ч55", "Ж55"]). Names that do not encode a combined prefix are returned as a single
    /// (normalized) element. A blank input yields an empty list.
    /// </summary>
    IReadOnlyList<string> Split(string courseName);
}
