namespace OrientDesk.BusinessLogic.Models;

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
}
