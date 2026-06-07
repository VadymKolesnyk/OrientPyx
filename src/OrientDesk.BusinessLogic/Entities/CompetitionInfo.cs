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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
