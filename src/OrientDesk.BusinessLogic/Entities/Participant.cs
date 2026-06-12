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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
