namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One scatter variant as edited on the Groups page: its display <see cref="Code"/> (e.g. "A") and the raw
/// course-order string (<see cref="CourseOrder"/>, space-separated codes, start/finish kept — exactly like
/// <c>GroupDaySettings.CourseOrder</c>). Flat read/write shape used by <c>ICompetitionEditorService</c> to
/// load and replace a group's variants; the service maps it to/from the <c>ScatterVariant</c> entity.
/// </summary>
public sealed record ScatterVariantRow(string Code, string CourseOrder);
