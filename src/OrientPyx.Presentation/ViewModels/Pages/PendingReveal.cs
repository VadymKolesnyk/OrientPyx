namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>A row a page is asked to show («Перейти»): its id and the cell to focus (a column key or a header's
/// localization key; null = any).</summary>
public sealed record PendingReveal(Guid Id, string? ColumnKey);
