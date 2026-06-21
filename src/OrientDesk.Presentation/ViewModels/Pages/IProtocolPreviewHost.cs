namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A page view-model that drives the shared protocol document preview (header + one section table with
/// drag-reorderable column headers). Implemented by both the results-protocol and start-protocol pages so
/// the preview View/code-behind is shared. The preview carries opaque string column keys; the host resolves
/// a key back to its own column enum when a header is dragged.
/// </summary>
public interface IProtocolPreviewHost
{
    /// <summary>The live preview model the View renders.</summary>
    ProtocolPreviewViewModel Preview { get; }

    /// <summary>
    /// Moves the column with key <paramref name="draggedKey"/> next to the column with key
    /// <paramref name="targetKey"/> — before it, or after it when <paramref name="insertAfter"/> is true.
    /// Both keys are resolved against the implementer's <b>full</b> column list (which may contain hidden
    /// columns the preview never shows), so the move is correct regardless of column visibility.
    /// </summary>
    void MoveColumnByKey(string draggedKey, string targetKey, bool insertAfter);
}
