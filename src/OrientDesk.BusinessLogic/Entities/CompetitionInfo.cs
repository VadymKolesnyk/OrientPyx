namespace OrientDesk.BusinessLogic.Entities;

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

    // --- Entry-fee settings (edited on the «Стартові внески» page; no fee calculation is wired yet) ---

    /// <summary>Whether a raised (late) start-entry fee applies after <see cref="RaisedFeeDeadline"/>.</summary>
    public bool RaisedFeeEnabled { get; set; }

    /// <summary>The raised start-entry fee amount, applied when <see cref="RaisedFeeEnabled"/> is on. Null = unset.</summary>
    public decimal? RaisedFeeAmount { get; set; }

    /// <summary>Date after which the raised fee applies (registrations past this pay the raised amount). Null = unset.</summary>
    public DateTimeOffset? RaisedFeeDeadline { get; set; }

    /// <summary>Base rental-chip price per day, the default unless a note-keyed override matches. Null = unset.</summary>
    public decimal? ChipRentalPricePerDay { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
