using System.Globalization;
using System.Text;
using System.Text.Json;
using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>The kind of an additional participant field (stored by name, matching <see cref="FieldKind.Name"/>).</summary>
public enum CustomFieldKind
{
    Text,
    Number,
    Bool,
    Choice
}

/// <summary>
/// An additional participant field as the app works with it: the stored <see cref="CustomFieldDef"/> with its kind
/// and choice options parsed. <see cref="Normalize"/> is the one place a raw value (typed, pasted, imported) is
/// turned into what is stored (Р-18, Р-19).
/// </summary>
public sealed record CustomField(
    string Key,
    string Label,
    CustomFieldKind Kind,
    IReadOnlyList<ChoiceOption> Options,
    string Source,
    int Order)
{
    /// <summary>The <c>Source</c> of a field the user created.</summary>
    public const string UserSource = "user";

    /// <summary>The <c>Source</c> prefix of a field a plugin declared (<c>plugin:&lt;id&gt;</c>).</summary>
    public const string PluginSourcePrefix = "plugin:";

    /// <summary>The import-target / table-column key prefix of an additional field (<c>field.&lt;key&gt;</c>).</summary>
    public const string ColumnPrefix = "field.";

    /// <summary>The plugin that declared the field; null for a user field.</summary>
    public string? PluginId => Source.StartsWith(PluginSourcePrefix, StringComparison.Ordinal)
        ? Source[PluginSourcePrefix.Length..]
        : null;

    /// <summary>
    /// Normalises a raw value for storage. Blank → empty. Choice: an option value or one of its synonyms (compared
    /// via <see cref="TextKey"/>, case-insensitive) becomes the option's value; anything else is kept as typed
    /// (Р-18). Number: comma or dot decimal separator → invariant text; a non-number is kept as typed (Р-19).
    /// Bool: common yes/no spellings → "1" / "0"; anything else is kept.
    /// </summary>
    public string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        switch (Kind)
        {
            case CustomFieldKind.Choice:
                return MatchOption(value)?.Value ?? value;
            case CustomFieldKind.Number:
                return TryParseNumber(value, out var number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : value;
            case CustomFieldKind.Bool:
                if (Truthy.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return "1";
                if (Falsy.Contains(value, StringComparer.OrdinalIgnoreCase))
                    return "0";
                return value;
            default:
                return value;
        }
    }

    /// <summary>True when a stored value does not fit the field (a choice outside the options, a non-number) —
    /// shown red in the tables (Р-18). Blank is always valid.</summary>
    public bool IsInvalid(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0)
            return false;
        return Kind switch
        {
            CustomFieldKind.Choice => MatchOption(v) is null,
            CustomFieldKind.Number => !TryParseNumber(v, out _),
            CustomFieldKind.Bool => v is not ("1" or "0"),
            _ => false
        };
    }

    /// <summary>The option whose value or a synonym matches <paramref name="value"/>; null when none.</summary>
    public ChoiceOption? MatchOption(string value) =>
        Options.FirstOrDefault(o => TextKey.Same(o.Value, value) || o.Aliases.Any(a => TextKey.Same(a, value)));

    private static readonly string[] Truthy = ["1", "+", "так", "т", "yes", "y", "true", "x", "✓"];
    private static readonly string[] Falsy = ["0", "-", "ні", "н", "no", "n", "false"];

    private static bool TryParseNumber(string value, out decimal number) =>
        decimal.TryParse(value.Replace(',', '.').Replace(" ", string.Empty), NumberStyles.Number,
            CultureInfo.InvariantCulture, out number);

    /// <summary>Builds the model from the stored row (unknown kind → Text; broken options JSON → none).</summary>
    public static CustomField From(CustomFieldDef def)
    {
        var kind = Enum.TryParse<CustomFieldKind>(def.Kind, ignoreCase: true, out var k) ? k : CustomFieldKind.Text;
        return new CustomField(def.Key, def.Label, kind, ParseOptions(def.OptionsJson), def.Source, def.Order);
    }

    /// <summary>The stored row for this model.</summary>
    public CustomFieldDef ToDef() => new()
    {
        Key = Key,
        Label = Label,
        Kind = Kind.ToString(),
        OptionsJson = Kind == CustomFieldKind.Choice ? SerializeOptions(Options) : string.Empty,
        Source = Source,
        Order = Order
    };

    /// <summary>The model's kind for a plugin-declared <see cref="FieldKind"/>.</summary>
    public static CustomFieldKind KindOf(FieldKind kind) =>
        Enum.TryParse<CustomFieldKind>(kind.Name, ignoreCase: true, out var k) ? k : CustomFieldKind.Text;

    public static string SerializeOptions(IReadOnlyList<ChoiceOption> options) =>
        options.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(options.Select(o => new StoredOption(o.Value, o.Aliases.ToList())).ToList());

    public static IReadOnlyList<ChoiceOption> ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredOption>>(json) ?? [];
            return stored
                .Where(o => !string.IsNullOrWhiteSpace(o.Value))
                .Select(o => new ChoiceOption(o.Value.Trim(),
                    (o.Aliases ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray()))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A stable field key generated from a display name (Р-14): Ukrainian letters transliterated, lower-case Latin
    /// letters and digits kept, anything else collapsed to '-'. Never empty ("field" when nothing is left).
    /// </summary>
    public static string KeyFromLabel(string label)
    {
        var sb = new StringBuilder();
        foreach (var ch in (label ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(ch);
            else if (Translit.TryGetValue(ch, out var latin))
                sb.Append(latin);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var key = sb.ToString().Trim('-');
        return key.Length == 0 ? "field" : key;
    }

    // Ukrainian (plus the Russian letters that show up in registration files) → Latin, per the 2010 passport table.
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "h", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['є'] = "ie",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l",
        ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ь'] = "", ['ю'] = "iu",
        ['я'] = "ia", ['\''] = "", ['’'] = "", ['ʼ'] = "", ['ё'] = "e", ['ы'] = "y", ['э'] = "e", ['ъ'] = ""
    };

    private sealed record StoredOption(string Value, List<string>? Aliases);
}
