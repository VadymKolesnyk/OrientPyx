namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Why a participant table's group cell is tinted: amber «без групи» (a day member with no group), red when the
/// runner's relay group takes its courses «за номером етапу» and their leg number has none the results can use. Mapped to
/// a brush by <see cref="Behaviors.GroupCellHighlight"/>; the cell's tooltip explains it.
/// </summary>
public enum GroupCellTint
{
    None,
    NoGroup,
    RelayCourse
}
