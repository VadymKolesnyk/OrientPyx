namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// The last column mapping the user confirmed for a CSV/Excel file with a given set of headers, so the next
/// import of the same layout is pre-mapped. <see cref="Json"/> maps an import target key to the header caption.
/// One row per header set (unique <see cref="HeaderSetHash"/>).
/// </summary>
public class ImportMappingRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string HeaderSetHash { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;
}
