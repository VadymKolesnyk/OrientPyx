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
}

/// <summary>Raised when readout content is not in a shape the parser can read.</summary>
public sealed class ReadoutFormatException : Exception
{
    public ReadoutFormatException(string message) : base(message) { }
}
