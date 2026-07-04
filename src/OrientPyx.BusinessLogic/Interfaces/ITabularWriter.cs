using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Writes a layer-neutral table (header + string rows, the same <see cref="CsvParticipantData"/> the
/// parsers produce) into a file format's bytes. The inverse of <see cref="ICsvParser"/> /
/// <see cref="ISpreadsheetParser"/>: those read a file into the table, this serialises the table back
/// out, so the participants table's on-screen view can be exported. Each concrete writer targets one
/// format (CSV, .xlsx). A CSV writer is pure text and lives in BusinessLogic; the .xlsx writer needs a
/// spreadsheet library and lives in DataAccess, mirroring how the parsers are split across the layers.
/// </summary>
public interface ITabularWriter
{
    /// <summary>The file format this writer produces (used to pick the right writer for the chosen format).</summary>
    ExportFormat Format { get; }

    /// <summary>
    /// Serialises <paramref name="data"/> (its header row, then one row per record) into the bytes of a
    /// file in this writer's format, ready to be written to disk.
    /// </summary>
    byte[] Write(CsvParticipantData data);
}
