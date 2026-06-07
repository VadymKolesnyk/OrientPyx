namespace OrientDesk.BusinessLogic.Entities;

/// <summary>A course (distance) with a sequence of controls. Placeholder.</summary>
public class Course
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
