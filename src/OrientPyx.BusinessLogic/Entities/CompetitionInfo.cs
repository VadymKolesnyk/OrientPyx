namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Metadata of a competition, stored as the single row inside that competition's event database.
/// The <see cref="Identifier"/> matches the folder name under the events path.
/// </summary>
public class CompetitionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Stable identifier; also the folder name under the events path.</summary>
    public string Identifier { get; set; } = string.Empty;

    public string Venue { get; set; } = string.Empty;

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

    // --- Entry-fee settings (edited on the «Стартові внески» page; used by the participant fee total)

    /// <summary>Whether a raised (late) start-entry fee applies.</summary>
    public bool RaisedFeeEnabled { get; set; }

    /// <summary>The raised start-entry fee amount, applied when <see cref="RaisedFeeEnabled"/> is on. Null = unset.</summary>
    public decimal? RaisedFeeAmount { get; set; }

    /// <summary>Base rental-chip price per day, the default unless a note-keyed override matches. Null = unset.</summary>
    public decimal? ChipRentalPricePerDay { get; set; }

    /// <summary>
    /// Whether the start-entry fee is paid per day rather than once for the whole competition. Off (the
    /// default) the payment is a single competition-level value on <see cref="Participant.Payment"/>,
    /// compared against the total fee across every day the athlete runs. On, each
    /// <see cref="ParticipantDay.Payment"/> carries that day's payment and is compared against that day's
    /// own share of the fee. Both columns are kept, so toggling the mode back and forth never loses data;
    /// the switch migrates the values across (see <c>SetPaymentPerDayAsync</c>).
    /// </summary>
    public bool PaymentPerDay { get; set; }

    // --- Officials (edited on the «Інформація» page; printed on the protocols)
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

    // --- Points (edited above the Groups table; a group may override per day)

    /// <summary>
    /// Competition-wide default points rule (правило нарахування очок). References an application-level
    /// <c>PointsRule</c> (app.db) by id; null = no default. A group on a given day may override it
    /// (see <see cref="GroupDaySettings.PointsRuleId"/>). Scoring with the rule is a later feature.
    /// </summary>
    public Guid? DefaultPointsRuleId { get; set; }

    // --- Participants page view

    /// <summary>
    /// Whether the participants page offers the roster («Мандатка») aggregate view. On (the default) the
    /// day selector carries the roster option ahead of the days; off, it lists the days alone and the page
    /// always shows one day. Toggled from the page's «Дії» menu and kept here so the choice survives a
    /// restart and travels with an export.
    /// </summary>
    public bool RosterEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
