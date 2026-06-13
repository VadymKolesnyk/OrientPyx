using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Parses a UOF participant file (the Ukrainian registration export, <c>&lt;UOFData&gt;</c> root)
/// into the layer-neutral <see cref="UofParticipantData"/>. Pure parsing: no files are opened here
/// and no entities are produced, so the result can feed the participant import.
/// </summary>
public interface IUofXmlParser
{
    /// <summary>
    /// Parses an XML document supplied as already-decoded text (the caller is responsible for decoding
    /// the bytes with the encoding declared in the XML prolog). Throws <see cref="UofXmlFormatException"/>
    /// when the content is not a recognised UOF document.
    /// </summary>
    UofParticipantData Parse(string xml);
}

/// <summary>Raised when an XML document is not a recognisable UOF participant file.</summary>
public sealed class UofXmlFormatException : Exception
{
    public UofXmlFormatException(string message) : base(message) { }
}
