namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One row of the draw result table: a competitor with the start time they were drawn. Shown after the
/// draw runs and saved back to the database on "Зберегти". Ordered by start time within the table.
/// </summary>
public sealed class DrawResultRowViewModel
{
    public DrawResultRowViewModel(
        Guid linkId,
        string groupName,
        string number,
        string fullName,
        string separationValue,
        TimeSpan startTime,
        string startTimeLabel)
    {
        LinkId = linkId;
        GroupName = groupName;
        Number = number;
        FullName = fullName;
        SeparationValue = separationValue;
        StartTime = startTime;
        StartTimeLabel = startTimeLabel;
    }

    /// <summary>The participant-day link the drawn start time is written to.</summary>
    public Guid LinkId { get; }

    public string GroupName { get; }
    public string Number { get; }
    public string FullName { get; }

    /// <summary>The value of the field the draw separated on (region/club/team), for the user to eyeball.</summary>
    public string SeparationValue { get; }

    public TimeSpan StartTime { get; }
    public string StartTimeLabel { get; }
}
