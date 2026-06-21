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

    /// <summary>Moves the column with the given key to <paramref name="targetIndex"/> in the on-page order.</summary>
    void MoveColumnByKey(string key, int targetIndex);
}
