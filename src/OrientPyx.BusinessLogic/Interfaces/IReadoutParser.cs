using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Parses a chip-readout file (supplied as text) into the layer-neutral <see cref="ChipReadData"/>.
/// Pure parsing: no files are opened here, so the caller decides where the text comes from (a file
/// picker, a polled file, …) and the same result can feed any consumer.
///
/// One implementation handles one file format. <see cref="CanParse"/> lets callers pick a parser
/// for a given file without committing to a format-resolver design before more formats exist.
/// </summary>
public interface IReadoutParser
{
    /// <summary>True when this parser recognises the supplied content as its format.</summary>
    bool CanParse(string content);

    /// <summary>
    /// Parses readout content. Throws <see cref="ReadoutFormatException"/> when the content is not
    /// in a shape this parser understands.
    /// </summary>
    ChipReadData Parse(string content);

    /// <summary>
    /// The line(s) a <b>new, empty</b> read-out file is seeded with so that the timing software, when it
    /// appends to a file that already exists, produces a file this parser reads. SPORTident ReaderUI writes
    /// its header row only into a file it creates itself; a file the app created first gets data rows only,
    /// which a header-keyed parser rejects. Null when the format needs no seeding.
    /// </summary>
    string? NewFileHeader => null;
}

/// <summary>Raised when readout content is not in a shape the parser can read.</summary>
public sealed class ReadoutFormatException : Exception
{
    public ReadoutFormatException(string message) : base(message) { }
}
