namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A sports rank (розряд) and the points it is worth — an application-level lookup shared across every
/// competition (it lives in the app database, not a per-event one). The default set is seeded on first
/// run; the list is editable on the Ranks page (rename, re-point, add, remove).
///
/// A participant references a rank only by its <b>name</b> (kept as free text in
/// <see cref="Participant.Rank"/>), so renaming or deleting a rank here never breaks existing
/// participant data — the stored text simply stops matching a known rank until re-picked.
/// </summary>
public class SportRank
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name (e.g. "МСМК", "I", "б/р"); unique (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Points the rank is worth.</summary>
    public double Points { get; set; }

    /// <summary>Display order; seeded ranks keep the canonical МСМК→б/р order.</summary>
    public int Order { get; set; }
}
