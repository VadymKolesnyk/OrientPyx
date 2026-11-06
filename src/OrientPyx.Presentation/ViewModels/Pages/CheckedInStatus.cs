namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// The "by status" filter tokens of the roster's «Номер» column — whether the participant has been through
/// registration («Мандатку пройдено», <see cref="ParticipantRosterRowViewModel.CheckedIn"/>). The column's
/// filter mode offers one checkbox per token, like the payment column's statuses.
/// </summary>
public static class CheckedInStatus
{
    public const string Yes = "CheckedIn";
    public const string No = "NotCheckedIn";

    /// <summary>Tokens with their labels, in display order, for <c>SheetColumn.StatusOptions</c>.</summary>
    public static IReadOnlyList<(string Token, string LabelKey)> FilterOptions { get; } =
    [
        (Yes, "Sheet.Filter.CheckedIn.Yes"),
        (No, "Sheet.Filter.CheckedIn.No")
    ];
}
