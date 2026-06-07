using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>A computed result line for the results table. Read model, not persisted.</summary>
public class ResultRow
{
    public int Place { get; set; }
    public string ParticipantName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public TimeSpan? Time { get; set; }
    public ResultStatus Status { get; set; } = ResultStatus.NotStarted;
}
