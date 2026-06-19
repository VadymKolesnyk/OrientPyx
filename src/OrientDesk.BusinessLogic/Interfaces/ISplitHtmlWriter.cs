using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Renders a <see cref="SplitExportDocument"/> into the bytes of a UTF-8 HTML file. The implementation lives
/// in DataAccess (it is an output writer, alongside the .docx and .xlsx writers), keeping the HTML string
/// work out of the presentation layer. The document is values-only and already localized, so the writer only
/// lays out the markup — a header, a group jump-nav, then a section per group (a set-course split table or a
/// scored per-runner passage).
/// </summary>
public interface ISplitHtmlWriter
{
    /// <summary>Serialises <paramref name="document"/> into the bytes of a UTF-8 .html file, ready to save.</summary>
    byte[] Write(SplitExportDocument document);
}
