namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Metadata of a competition, stored as the single row inside that competition's event database.
/// The <see cref="Identifier"/> matches the folder name under the events path.
/// </summary>
public class CompetitionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>User-friendly display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Stable identifier; also the folder name under the events path.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Venue / location of the competition.</summary>
    public string Venue { get; set; } = string.Empty;

    /// <summary>Organisation running the competition.</summary>
    public string Organisation { get; set; } = string.Empty;

    /// <summary>Optional first day of the competition.</summary>
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>Optional last day of the competition.</summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Whether this competition is hidden from the selection list by default. A per-competition
    /// convenience toggle (kept in the event database so it travels with an export); the selection
    /// page has a switch to reveal hidden competitions and unhide them.
    /// </summary>
    public bool IsHidden { get; set; }

    // --- Entry-fee settings (edited on the «Стартові внески» page; used by the participant fee total) ---

    /// <summary>Whether a raised (late) start-entry fee applies.</summary>
    public bool RaisedFeeEnabled { get; set; }

    /// <summary>The raised start-entry fee amount, applied when <see cref="RaisedFeeEnabled"/> is on. Null = unset.</summary>
    public decimal? RaisedFeeAmount { get; set; }

    /// <summary>Base rental-chip price per day, the default unless a note-keyed override matches. Null = unset.</summary>
    public decimal? ChipRentalPricePerDay { get; set; }

    // --- Officials (edited on the «Інформація» page; printed on the protocols) ---
    // Each named official has an optional judge category (суддівська категорія). The course-setter
    // (начальник дистанції) is the competition-wide default; a group on a given day may override it
    // (see GroupDaySettings.CourseSetter). Jury is a free multi-line text — one member per line — since
    // a jury is a small, ad-hoc list rather than a fixed role.

    /// <summary>Начальник дистанції — competition-wide default course-setter name. Blank = none.</summary>
    public string CourseSetter { get; set; } = string.Empty;

    /// <summary>Optional judge category (суддійська категорія) for the course-setter. Blank = none.</summary>
    public string CourseSetterCategory { get; set; } = string.Empty;

    /// <summary>Головний суддя — chief judge name. Blank = none.</summary>
    public string ChiefJudge { get; set; } = string.Empty;

    /// <summary>Optional judge category for the chief judge. Blank = none.</summary>
    public string ChiefJudgeCategory { get; set; } = string.Empty;

    /// <summary>Головний секретар — chief secretary name. Blank = none.</summary>
    public string ChiefSecretary { get; set; } = string.Empty;

    /// <summary>Optional judge category for the chief secretary. Blank = none.</summary>
    public string ChiefSecretaryCategory { get; set; } = string.Empty;

    /// <summary>Журі — free multi-line text, one jury member per line (a category may be typed inline).
    /// Blank = no jury.</summary>
    public string Jury { get; set; } = string.Empty;

    // --- Points (edited above the Groups table; a group may override per day) ---

    /// <summary>
    /// Competition-wide default points rule (правило нарахування очок). References an application-level
    /// <c>PointsRule</c> (app.db) by id; null = no default. A group on a given day may override it
    /// (see <see cref="GroupDaySettings.PointsRuleId"/>). Scoring with the rule is a later feature.
    /// </summary>
    public Guid? DefaultPointsRuleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
