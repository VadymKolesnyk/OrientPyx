using System.Globalization;

namespace OrientPyx.Plugins.Sdk;

/// <summary>Where a setting's value is stored (Р-11).</summary>
public enum SettingScope
{
    /// <summary>One value per (day, discipline).</summary>
    Day,

    /// <summary>One value for the whole competition, shared by every day with this discipline.</summary>
    Competition
}

/// <summary>The editor and value type of a setting.</summary>
public enum SettingKind
{
    Int,
    Decimal,
    Bool,
    Text,
    Choice,

    /// <summary>A table of rows (see <see cref="TableValue"/>), edited in its own sheet.</summary>
    Table
}

/// <summary>A discipline setting. <see cref="Label"/> is a key of the plugin's strings or plain text.</summary>
public sealed record SettingDef(string Key, string Label, SettingKind Kind, object? DefaultValue, SettingScope Scope = SettingScope.Day)
{
    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    /// <summary>The options of a <see cref="SettingKind.Choice"/> setting.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The columns of a <see cref="SettingKind.Table"/> setting.</summary>
    public IReadOnlyList<TableColumnDef> Columns { get; init; } = [];
}

/// <summary>Factories for <see cref="SettingDef"/>.</summary>
public static class Setting
{
    public static SettingDef Int(string key, string label, int defaultValue = 0, int? min = null, int? max = null,
        SettingScope scope = SettingScope.Day) =>
        new(key, label, SettingKind.Int, defaultValue, scope) { Min = min, Max = max };

    public static SettingDef Decimal(string key, string label, decimal defaultValue = 0m, decimal? min = null,
        decimal? max = null, SettingScope scope = SettingScope.Day) =>
        new(key, label, SettingKind.Decimal, defaultValue, scope) { Min = min, Max = max };

    public static SettingDef Bool(string key, string label, bool defaultValue = false, SettingScope scope = SettingScope.Day) =>
        new(key, label, SettingKind.Bool, defaultValue, scope);

    public static SettingDef Text(string key, string label, string defaultValue = "", SettingScope scope = SettingScope.Day) =>
        new(key, label, SettingKind.Text, defaultValue, scope);

    public static SettingDef Choice(string key, string label, IReadOnlyList<string> options, string? defaultValue = null,
        SettingScope scope = SettingScope.Day) =>
        new(key, label, SettingKind.Choice, defaultValue ?? (options.Count > 0 ? options[0] : string.Empty), scope)
        {
            Choices = options
        };

    /// <summary>A table setting: rows of cells under <paramref name="columns"/>, e.g. a coefficient table.</summary>
    public static SettingDef Table(string key, string label, IReadOnlyList<TableColumnDef> columns, TableValue defaultValue,
        SettingScope scope = SettingScope.Day) =>
        new(key, label, SettingKind.Table, defaultValue, scope)
        {
            Columns = columns
        };
}

/// <summary>
/// A discipline's effective setting values: what was saved, else each setting's default. Values are typed by
/// <see cref="SettingKind"/> (int, decimal, bool, string).
/// </summary>
public sealed class SettingValues
{
    public static readonly SettingValues Empty = new(new Dictionary<string, object?>(), []);

    private readonly IReadOnlyDictionary<string, object?> _values;
    private readonly Dictionary<string, SettingDef> _defs;

    public SettingValues(IReadOnlyDictionary<string, object?> values, IEnumerable<SettingDef> defs)
    {
        _values = values;
        _defs = new Dictionary<string, SettingDef>(StringComparer.Ordinal);
        foreach (var def in defs)
            _defs.TryAdd(def.Key, def);
    }

    /// <summary>The saved value, else the setting's default (null when the key is unknown).</summary>
    public object? this[string key] =>
        _values.TryGetValue(key, out var v) && v is not null ? v
        : _defs.TryGetValue(key, out var def) ? def.DefaultValue
        : null;

    /// <summary>The value converted to <typeparamref name="T"/> (default when missing or not convertible).</summary>
    public T Get<T>(string key) => ConvertTo<T>(this[key])!;

    /// <summary>Converts a stored value to <typeparamref name="T"/>, tolerating numeric widening and strings.</summary>
    public static T? ConvertTo<T>(object? value)
    {
        if (value is null)
            return default;
        if (value is T typed)
            return typed;

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            if (target == typeof(TimeSpan) && value is string s && TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
                return (T)(object)ts;
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch
        {
            return default;
        }
    }
}
