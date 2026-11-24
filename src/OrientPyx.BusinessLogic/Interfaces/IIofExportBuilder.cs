using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Turns the raw <see cref="IofExportData"/> gathered by the editor into an IOF Data Standard 3.0 document tree —
/// results with splits or the start list — of one day or several. Pure: no files, no database, no strategies.
/// </summary>
public interface IIofExportBuilder
{
    IofResultList BuildResultList(IofExportData data, IofExportOptions options);

    IofStartList BuildStartList(IofExportData data, IofExportOptions options);
}
