namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// One participant's value of an additional field (<see cref="CustomFieldDef"/>). Primary key
/// (<see cref="ParticipantId"/>, <see cref="Key"/>); a blank value is not stored. Deleted with the participant
/// (Р-16) and with the field (Р-15).
/// </summary>
public class ParticipantFieldValue
{
    public Guid ParticipantId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
