using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Parses a delimited participant CSV file (already-decoded text) into the layer-neutral
/// <see cref="CsvParticipantData"/> — its header and rows, with no mapping to our fields applied yet.
/// Pure parsing: no files are opened here and no entities are produced, so the result can drive the
/// column-mapping modal and then the import.
/// </summary>
public interface ICsvParser
{
    /// <summary>
    /// Parses CSV supplied as already-decoded text (the caller decodes the bytes). The delimiter
    /// (comma or semicolon) is sniffed from the header line, quoted fields and embedded newlines are
    /// honoured. Throws <see cref="CsvFormatException"/> when the text has no header row.
    /// </summary>
    CsvParticipantData Parse(string csv);
}

/// <summary>Raised when a CSV document has no usable header row.</summary>
public sealed class CsvFormatException : Exception
{
    public CsvFormatException(string message) : base(message) { }
}
