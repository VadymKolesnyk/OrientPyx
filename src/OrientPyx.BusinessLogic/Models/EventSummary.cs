namespace OrientPyx.BusinessLogic.Models;

/// <summary>Row shown in the competition selection table, built by scanning the events folder.</summary>
public class EventSummary
{
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;

    /// <summary>Absolute path to the competition's folder.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public int DayCount { get; set; }

    /// <summary>First day of the competition, if known.</summary>
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>Last day of the competition, if known.</summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Human-readable date span for the selection list: a single date when the
    /// competition spans one day (or both ends coincide), a "start – end" range
    /// otherwise, and empty when no dates are set.
    /// </summary>
    public string DateRange
    {
        get
        {
            if (StartDate is not { } start)
                return EndDate is { } onlyEnd ? Format(onlyEnd) : string.Empty;

            if (EndDate is not { } end || end.Date == start.Date)
                return Format(start);

            return $"{Format(start)} – {Format(end)}";
        }
    }

    private static string Format(DateTimeOffset value) => value.ToString("dd.MM.yyyy");
}
