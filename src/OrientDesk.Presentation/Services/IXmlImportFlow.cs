namespace OrientDesk.Presentation.Services;

/// <summary>
/// The single "Import from XML" flow shared by the control-points and groups pages. One IOF file
/// carries both controls and courses, so one action imports <b>both</b>: it parses the file, shows
/// one options modal with two toggles (replace all control points / update existing groups), then
/// writes control points and groups for the current day. Both pages call this identically; only the
/// reload afterwards differs, so each page reloads itself when this returns true.
/// </summary>
public interface IXmlImportFlow
{
    /// <summary>
    /// Runs the full import for the supplied IOF XML text. Returns true when the user confirmed and
    /// the import ran (so the caller should reload), or false when the file was unreadable/invalid,
    /// no day is selected, or the user cancelled.
    /// </summary>
    Task<bool> RunAsync(string xml);
}
