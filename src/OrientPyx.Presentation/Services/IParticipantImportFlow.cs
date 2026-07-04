namespace OrientPyx.Presentation.Services;

/// <summary>
/// The "Import participants from XML" flow on the participants page. Parses a UOF file, asks whether
/// to clear the existing participant database first (one toggle, like the control-points "replace"
/// option), then runs the import for the current competition. The page reloads itself when this
/// returns true.
/// </summary>
public interface IParticipantImportFlow
{
    /// <summary>
    /// Runs the full participant import for the supplied UOF XML text. Returns true when the user
    /// confirmed and the import ran (so the caller should reload), or false when the file was
    /// unreadable/invalid, no competition is selected, or the user cancelled.
    /// </summary>
    Task<bool> RunAsync(string xml);
}
