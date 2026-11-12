namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// An additional field the participant import can fill: one of the competition's fields, or a field a switched-on
/// plugin declares that the competition does not have yet (<see cref="Exists"/> false — the import creates it once a
/// file brings values for it). A file column / XML element is the field's when its name is the field's label, its
/// key, or one of the plugin's import names (<see cref="FieldImport.Names"/>), compared via <see cref="TextKey"/>.
/// </summary>
/// <param name="PluginName">The declaring plugin's name; null for a user field or a plugin that is not loaded.</param>
public sealed record ImportField(CustomField Field, FieldImport? Import, string? PluginName, bool Exists)
{
    public string Key => Field.Key;

    /// <summary>True when a column header / XML element or attribute named <paramref name="name"/> carries the field.</summary>
    public bool Matches(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (TextKey.Same(Field.Label, name) || TextKey.Same(Field.Key, name) || Import?.Matches(name) == true);

    /// <summary>A value as written in a file → the value to store: the plugin's translation, else the value itself.</summary>
    public string Translate(string? value) => Import?.Translate(value) ?? (value ?? string.Empty).Trim();

    /// <summary>The record's value for the field among its XML values (<see cref="UofParticipant.Raw"/>), with the
    /// name it was found under; null when the record has none.</summary>
    public (string Name, string Value)? RawValue(UofParticipant participant)
    {
        foreach (var (name, value) in participant.Raw)
            if (value.Trim().Length > 0 && Matches(name))
                return (name, value.Trim());
        return null;
    }
}

/// <summary>An additional field an XML file can fill (offered as a checkbox in the import window).</summary>
/// <param name="SourceName">The element / attribute it is read from («Pol»).</param>
/// <param name="Count">How many records of the file carry a value for it.</param>
/// <param name="Exists">The competition already has the field (else the import creates it).</param>
public sealed record ImportFileField(string Key, string Label, string? PluginName, string SourceName, int Count, bool Exists);
