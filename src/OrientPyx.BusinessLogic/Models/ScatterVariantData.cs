namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One scatter («розсіювання») course variant reduced to what a discipline strategy needs to judge it: a
/// display <see cref="Code"/> (e.g. "A") and the ordered required <see cref="Controls"/> — the variant's
/// course order already stripped of start/finish markers and the day's disabled («проблемні») controls,
/// exactly like <c>FinishContext.ExpectedControls</c>. Carried on <see cref="FinishContext"/> and
/// <see cref="SplitsContext"/> so the scatter strategy can pick the runner's best-matching variant.
/// </summary>
public sealed record ScatterVariantData(string Code, IReadOnlyList<string> Controls);

/// <summary>
/// One scatter variant as edited on the Groups page: its display <see cref="Code"/> (e.g. "A") and the raw
/// course-order string (<see cref="CourseOrder"/>, space-separated codes, start/finish kept — exactly like
/// <c>GroupDaySettings.CourseOrder</c>). Flat read/write shape used by <c>ICompetitionEditorService</c> to
/// load and replace a group's variants; the service maps it to/from the <c>ScatterVariant</c> entity.
/// </summary>
public sealed record ScatterVariantRow(string Code, string CourseOrder);
