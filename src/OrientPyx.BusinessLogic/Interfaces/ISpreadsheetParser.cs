using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Reads a spreadsheet workbook (.xlsx) into the same layer-neutral <see cref="CsvParticipantData"/>
/// the CSV parser produces — its first row is the header, the rest are data rows — so an Excel import
/// reuses the entire column-mapping + import path. The concrete reader (a spreadsheet library) lives
/// in DataAccess, since opening a document format is infrastructure; BusinessLogic only owns this
/// abstraction.
/// </summary>
public interface ISpreadsheetParser
{
    /// <summary>
    /// Parses the raw bytes of an .xlsx workbook. Reads the first worksheet: its first non-empty row
    /// is the header, every following row a record (cells stringified, dates as dd.MM.yyyy). Throws
    /// <see cref="SpreadsheetFormatException"/> when the bytes are not a readable workbook or it has
    /// no header row.
    /// </summary>
    CsvParticipantData Parse(byte[] bytes);
}

/// <summary>Raised when a workbook cannot be read or has no usable header row.</summary>
public sealed class SpreadsheetFormatException : Exception
{
    public SpreadsheetFormatException(string message) : base(message) { }

    public SpreadsheetFormatException(string message, Exception inner) : base(message, inner) { }
}
