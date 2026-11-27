namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// What <c>ICompetitionEditorService.MergeParticipantsAsync</c> did: the days the merged person's entry moved to the kept
/// one, the days both ran (the kept one's entry stays, the merged one's is dropped), the kept one's blank fields that
/// were filled (<c>bib</c>, <c>club</c>, <c>field.&lt;key&gt;</c> …) and how many discounts were added.
/// </summary>
public sealed record ParticipantMergeResult(
    IReadOnlyList<int> MovedDays,
    IReadOnlyList<int> DroppedDays,
    IReadOnlyList<string> FilledFields,
    int DiscountsAdded);
