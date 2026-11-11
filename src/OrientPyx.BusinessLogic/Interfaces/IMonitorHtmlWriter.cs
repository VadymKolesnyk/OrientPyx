using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Renders a <see cref="MonitorDocument"/> into the bytes of a self-contained, modern UTF-8 HTML file for
/// the on-screen results monitor: one group section per chosen group, with inlined CSS and a small inlined
/// script that smoothly auto-scrolls through the results and keeps them current. The implementation
/// lives in DataAccess (an output writer, alongside the split/.docx/.xlsx writers); the document is
/// values-only and already localized so the writer only lays out markup. No web server is needed — the page
/// works opened straight from disk on a venue screen.
/// </summary>
public interface IMonitorHtmlWriter
{
    /// <summary>Serialises <paramref name="document"/> into the bytes of a UTF-8 .html file, ready to save.
    /// <paramref name="dataFileName"/> names the live-data script beside it (<see cref="WriteData"/>,
    /// <see cref="MonitorFile.DataFileName"/>): the open page loads it every few seconds and swaps the header and the
    /// groups in place, keeping the scroll position. Without it (or while that file can't be read) the page reloads
    /// itself every <see cref="MonitorDocument.RefreshSeconds"/>, at the top of a scroll cycle.</summary>
    byte[] Write(MonitorDocument document, string? dataFileName = null);

    /// <summary>The live-data script for a page written by <see cref="Write"/>: the same header and groups as one
    /// JavaScript call the page picks up. A page whose timing (refresh / scroll) differs reloads itself.</summary>
    byte[] WriteData(MonitorDocument document);
}
