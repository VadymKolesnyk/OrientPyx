namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// The file format the participants-table export writes to. The user picks one in the export modal;
/// the matching <see cref="OrientDesk.BusinessLogic.Interfaces.ITabularWriter"/> serialises the view.
/// </summary>
public enum ExportFormat
{
    /// <summary>A delimited text file (.csv), UTF-8 with BOM, semicolon-separated.</summary>
    Csv,

    /// <summary>An Excel workbook (.xlsx) — one worksheet, header row then data rows.</summary>
    Excel
}
