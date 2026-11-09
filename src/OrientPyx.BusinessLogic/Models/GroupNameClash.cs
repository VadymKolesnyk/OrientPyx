namespace OrientPyx.BusinessLogic.Models;

/// <summary>A group name already taken: the group that has it (<paramref name="ExistingName"/>) and the renamed group's
/// current name (<paramref name="CurrentName"/>).</summary>
public sealed record GroupNameClash(string ExistingName, string CurrentName);
