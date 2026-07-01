using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Renders a <see cref="MonitorDocument"/> into the bytes of a self-contained, modern UTF-8 HTML file for
/// the on-screen results monitor: one group section per chosen group, with inlined CSS and a small inlined
/// script that auto-refreshes the page and smoothly auto-scrolls through the results. The implementation
/// lives in DataAccess (an output writer, alongside the split/.docx/.xlsx writers); the document is
/// values-only and already localized so the writer only lays out markup. No web server is needed — the page
/// reloads itself, so the file can be opened straight from disk on a venue screen.
/// </summary>
public interface IMonitorHtmlWriter
{
    /// <summary>Serialises <paramref name="document"/> into the bytes of a UTF-8 .html file, ready to save.</summary>
    byte[] Write(MonitorDocument document);
}
