namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The participant fields a group's discipline on a day requires (<c>Discipline.RequiredFields</c> of a plugin —
/// e.g. «gender» for the weighted relay): the participant tables tint an empty one red for that group's members.
/// </summary>
public sealed record RequiredFieldsEntry(Guid DayId, Guid GroupId, IReadOnlyList<string> FieldKeys);
