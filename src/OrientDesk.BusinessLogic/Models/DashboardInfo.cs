namespace OrientDesk.BusinessLogic.Models;

/// <summary>Summary shown on the dashboard. Placeholder data for now.</summary>
public class DashboardInfo
{
    public string ApplicationName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ParticipantCount { get; set; }
    public int GroupCount { get; set; }
}
