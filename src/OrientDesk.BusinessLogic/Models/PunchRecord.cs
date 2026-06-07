namespace OrientDesk.BusinessLogic.Models;

/// <summary>A single control punch read from a SportIdent chip. Transient import model, not persisted yet.</summary>
public class PunchRecord
{
    public string ChipNumber { get; set; } = string.Empty;
    public int ControlCode { get; set; }
    public DateTimeOffset PunchedAt { get; set; }
}
