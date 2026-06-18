using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Renders a <see cref="ResultProtocolDocument"/> into a Word (.docx) file's bytes. The implementation
/// lives in DataAccess (it uses a document library, which BusinessLogic must not reference), mirroring how
/// the .xlsx <see cref="ITabularWriter"/> is split out of the layer-neutral CSV one. The document is
/// values-only and already localized, so the writer only lays the content out — header, then a table per
/// group section.
/// </summary>
public interface IResultProtocolWriter
{
    /// <summary>Serialises <paramref name="document"/> into the bytes of a .docx file, ready to be saved.</summary>
    byte[] Write(ResultProtocolDocument document);
}
