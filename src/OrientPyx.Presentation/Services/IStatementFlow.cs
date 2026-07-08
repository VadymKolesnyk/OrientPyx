using OrientPyx.Presentation.Controls;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Opens the participant-statement («відомість») modal for the participants page. Captures the currently-shown
/// rows (participant ids, in on-screen order) and the applied-filter summary from the given
/// <see cref="SheetTable"/>, fetches the statement data scoped to a single day (day mode) or the whole roster
/// (all days), builds the modal view-model and shows it. Kept out of the participants VM so that VM never
/// references the table directly (the page code-behind hands it in), mirroring <see cref="IParticipantExportFlow"/>.
/// </summary>
public interface IStatementFlow
{
    /// <summary>
    /// Opens the statement modal for the given table's current view. <paramref name="dayId"/> is the day to
    /// scope per-day fields to (day mode), or null to gather them across all days and join with " / " (roster
    /// mode). A no-op when no competition is selected or the table has no rows.
    /// </summary>
    Task OpenAsync(SheetTable table, Guid? dayId);

    /// <summary>
    /// Prints the statement for the given table's current view straight to the configured A4 printer — no
    /// preview/settings modal (the Ctrl+Shift+P «швидкий друк» path). Uses the saved statement template (per
    /// competition, seeded from the app default) and the saved A4 printer. When no A4 printer is configured yet,
    /// falls back to opening the printer-picker modal once so the first print can still go through. A no-op when
    /// no competition is selected, the table has no rows, or printing is unsupported on this platform.
    /// </summary>
    Task PrintDirectAsync(SheetTable table, Guid? dayId);
}
