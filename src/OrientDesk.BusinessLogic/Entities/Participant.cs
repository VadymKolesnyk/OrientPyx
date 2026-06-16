namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level competitor. Identity (full name, number, rank, coach, birth date) is
/// shared across every day of the competition; a participant's presence on a given day is expressed
/// by a <see cref="ParticipantDay"/> row referencing it. The number is unique per competition
/// (enforced in the service layer), and only when non-blank.
/// </summary>
public class Participant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Full name (ПІБ); surname and given name held together in one field.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>Bib / start number; unique per competition when non-blank. Free text.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Sports rank (КМС, I, II, …). Free text for now.</summary>
    public string Rank { get; set; } = string.Empty;

    /// <summary>Coach(es); free text in a single field.</summary>
    public string Coach { get; set; } = string.Empty;

    /// <summary>Date of birth; optional.</summary>
    public DateTimeOffset? BirthDate { get; set; }

    /// <summary>The region (place) this participant comes from; optional, null = none.
    /// Region is competition-level (shared across days), like the other identity fields.</summary>
    public Guid? RegionId { get; set; }

    /// <summary>The club this participant belongs to; optional, null = none. Competition-level.</summary>
    public Guid? ClubId { get; set; }

    /// <summary>The sports school (ДЮСШ) this participant attends; optional, null = none. Competition-level.</summary>
    public Guid? DusshId { get; set; }

    /// <summary>Team representative / contact (Представник). Free text.</summary>
    public string Representative { get; set; } = string.Empty;

    /// <summary>FSOU (Федерація Спортивного Орієнтування України) code. Free text.</summary>
    public string FsouCode { get; set; } = string.Empty;

    /// <summary>Whether the participant is a member of the FSOU.</summary>
    public bool IsFsouMember { get; set; }

    /// <summary>Payment note (Оплата). Free text.</summary>
    public string Payment { get; set; } = string.Empty;

    /// <summary>
    /// Team name (used by team disciplines such as rogaine); empty otherwise. Competition-level: a
    /// competitor's team is the same across every day they run, like the other identity fields.
    /// </summary>
    public string Team { get; set; } = string.Empty;

    /// <summary>
    /// Whether this participant is charged the raised (late) start-entry fee instead of their group's
    /// base fee. Only meaningful when the competition has <see cref="CompetitionInfo.RaisedFeeEnabled"/>
    /// turned on. Defaults false.
    /// </summary>
    public bool PaysRaisedFee { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
