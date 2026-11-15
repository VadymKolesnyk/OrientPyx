namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One named course of a group as edited on the Groups page (a scatter variant or a relay leg course): its display
/// <see cref="Code"/> (e.g. "A"), the raw course-order string (<see cref="CourseOrder"/>, space-separated codes,
/// start/finish kept — exactly like <c>GroupDaySettings.CourseOrder</c>) and, for a relay «своя на кожен етап», the
/// <see cref="Leg"/> it is run on. Flat read/write shape used by <c>ICompetitionEditorService</c> to load and replace a
/// group's courses; the service maps it to/from the <c>GroupCourseVariant</c> entity.
/// </summary>
public sealed record CourseVariantRow(string Code, string CourseOrder, int? Leg = null);
