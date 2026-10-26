namespace OrientPyx.Plugins.Sdk;

/// <summary>One option of a choice field, with synonyms that import and manual entry fold to <see cref="Value"/>
/// (Р-18). A plain string converts implicitly: <c>FieldKind.Choice("Ч", "Ж")</c>.</summary>
public sealed record ChoiceOption(string Value, params string[] Aliases)
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
