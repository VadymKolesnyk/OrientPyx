namespace OrientDesk.BusinessLogic.Entities;

/// <summary>An age/category group participants compete in. Placeholder.</summary>
public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
