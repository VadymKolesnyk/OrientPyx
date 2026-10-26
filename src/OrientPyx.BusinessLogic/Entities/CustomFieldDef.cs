namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// An additional participant field of a competition (Р-13: one value per participant across all days). Created by
/// the user or declared by a plugin (<see cref="Source"/> = <c>plugin:&lt;id&gt;</c>, Р-17). The key is fixed once
/// created (Р-14); the label may be renamed. Values live in <see cref="ParticipantFieldValue"/>.
/// </summary>
public class CustomFieldDef
{
    /// <summary>Stable key (Latin letters, digits, '-'), e.g. <c>gender</c>. Primary key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Display name (column header). Free text.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Kind name: Text | Number | Bool | Choice (stored as text).</summary>
    public string Kind { get; set; } = "Text";

    /// <summary>The choice options with their synonyms, as JSON (<c>[{"Value":"Ч","Aliases":["M"]}]</c>); empty otherwise.</summary>
    public string OptionsJson { get; set; } = string.Empty;

    /// <summary><c>user</c>, or <c>plugin:&lt;id&gt;</c> for a field a plugin declared.</summary>
    public string Source { get; set; } = "user";

    /// <summary>Display order among the competition's fields.</summary>
    public int Order { get; set; }
}
