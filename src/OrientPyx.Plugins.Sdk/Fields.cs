namespace OrientPyx.Plugins.Sdk;

/// <summary>One option of a choice field. Import and manual entry fold a value to it regardless of case and of
/// look-alike Latin/Cyrillic letters (Р-18). A plain string converts implicitly: <c>FieldKind.Choice("Ч", "Ж")</c>.</summary>
public sealed record ChoiceOption(string Value)
{
    public static implicit operator ChoiceOption(string value) => new(value);
}

/// <summary>The type of an additional participant field.</summary>
public sealed record FieldKind
{
    private FieldKind(string name, IReadOnlyList<ChoiceOption> options)
    {
        Name = name;
        Options = options;
    }

    /// <summary>"Text", "Number", "Bool" or "Choice" — the stored kind name.</summary>
    public string Name { get; }

    /// <summary>The options of a choice field; empty otherwise.</summary>
    public IReadOnlyList<ChoiceOption> Options { get; }

    public static FieldKind Text { get; } = new("Text", []);

    public static FieldKind Number { get; } = new("Number", []);

    public static FieldKind Bool { get; } = new("Bool", []);

    public static FieldKind Choice(params ChoiceOption[] options) => new("Choice", options);
}

/// <summary>
/// How the participant import fills an additional field a plugin declares (see
/// <see cref="IPluginBuilder.AddParticipantField(string, string, FieldKind, FieldImport)"/>): the names the field goes
/// by in imported files, and the file values that stand for a value of the field. A name is a CSV/Excel column
/// header (the column is pre-selected in the mapping window) or the name of an XML element or attribute of a
/// participant record — UOF <c>&lt;Pol&gt;</c> of a <c>&lt;Sportsman&gt;</c>, IOF <c>sex</c> of a <c>&lt;Person&gt;</c>.
/// Names and values are compared regardless of case and of look-alike Latin/Cyrillic letters; a value with no
/// translation is imported as written (and then folded to a choice option as typed text is).
/// </summary>
/// <example><code>
/// FieldImport.From("Pol", "sex", "Стать").Value("1", "Ч").Value("0", "Ж")
/// </code></example>
public sealed class FieldImport
{
    private readonly KeyValuePair<string, string>[] _values;

    private FieldImport(string[] names, KeyValuePair<string, string>[] values)
    {
        Names = names;
        _values = values;
    }

    /// <summary>The names the field goes by in imported files (column headers, XML element/attribute names).</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The value translations: a value as written in a file → the value stored.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values => _values;

    /// <summary>A field imported from a column / element / attribute with one of <paramref name="names"/>.</summary>
    public static FieldImport From(params string[] names) =>
        new(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray(), []);

    /// <summary>A copy that also stores <paramref name="value"/> where a file says <paramref name="fileValue"/>
    /// (UOF <c>&lt;Pol&gt;0&lt;/Pol&gt;</c> → «Ж»). The first translation of a file value wins.</summary>
    public FieldImport Value(string fileValue, string value) =>
        string.IsNullOrWhiteSpace(fileValue)
            ? this
            : new(Names.ToArray(), [.. _values, new(fileValue.Trim(), value ?? string.Empty)]);

    /// <summary>True when <paramref name="name"/> is one of <see cref="Names"/>.</summary>
    public bool Matches(string? name) => Names.Any(n => Toolkit.TextKey.Same(n, name));

    /// <summary>The stored value for a value read from a file: its translation, else the value itself (trimmed).</summary>
    public string Translate(string? fileValue)
    {
        var raw = (fileValue ?? string.Empty).Trim();
        foreach (var (from, to) in _values)
            if (Toolkit.TextKey.Same(from, raw))
                return to;
        return raw;
    }
}
