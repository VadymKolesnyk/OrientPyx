using System.Globalization;
using System.Text.Json;
using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Reads and writes a discipline's saved settings (<see cref="PluginSettingsRow"/>, Р-11): day-scoped values come
/// from the day's row, competition-scoped values from the row with no day; anything missing is the setting's
/// default. Values are typed by <see cref="SettingKind"/> (int, decimal, bool, string).
/// </summary>
public static class DisciplineSettings
{
    /// <summary>The effective values of <paramref name="strategy"/> on <paramref name="dayId"/>.</summary>
    public static SettingValues Resolve(IDisciplineStrategy strategy, IEnumerable<PluginSettingsRow> rows, Guid dayId)
    {
        var defs = strategy.Settings;
        if (defs.Count == 0)
            return SettingValues.Empty;

        var dayValues = new Dictionary<string, object?>();
        var competitionValues = new Dictionary<string, object?>();
        foreach (var row in rows)
        {
            if (row.DisciplineId != strategy.Id)
                continue;
            if (row.EventDayId == dayId)
                dayValues = Parse(row.Json, defs);
            else if (row.EventDayId is null)
                competitionValues = Parse(row.Json, defs);
        }

        var merged = new Dictionary<string, object?>();
        foreach (var def in defs)
        {
            var source = def.Scope == SettingScope.Competition ? competitionValues : dayValues;
            if (source.TryGetValue(def.Key, out var value))
                merged[def.Key] = value;
        }
        return new SettingValues(merged, defs);
    }

    /// <summary>Parses a saved JSON object into typed values; unknown keys and unreadable values are dropped.</summary>
    public static Dictionary<string, object?> Parse(string? json, IReadOnlyList<SettingDef> defs)
    {
        var values = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(json))
            return values;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return values;
            foreach (var def in defs)
            {
                if (doc.RootElement.TryGetProperty(def.Key, out var element) && Read(element, def.Kind) is { } value)
                    values[def.Key] = value;
            }
        }
        catch (JsonException)
        {
            // A hand-edited or stale value falls back to the defaults.
        }
        return values;
    }

    /// <summary>Serialises the values of the settings in <paramref name="defs"/> as a JSON object.</summary>
    public static string Serialize(IReadOnlyDictionary<string, object?> values, IEnumerable<SettingDef> defs)
    {
        var output = new Dictionary<string, object?>();
        foreach (var def in defs)
            if (values.TryGetValue(def.Key, out var value))
                output[def.Key] = value;
        return JsonSerializer.Serialize(output);
    }

    /// <summary>Coerces an edited value to the setting's type (null when it cannot be read).</summary>
    public static object? Coerce(object? value, SettingDef def) => value switch
    {
        null => null,
        _ => def.Kind switch
        {
            SettingKind.Int => ToDecimal(value) is { } d ? (int)Math.Round(Clamp(d, def)) : null,
            SettingKind.Decimal => ToDecimal(value) is { } d ? Clamp(d, def) : null,
            SettingKind.Bool => value is bool b ? b : bool.TryParse(value.ToString(), out var pb) ? pb : null,
            _ => value.ToString()
        }
    };

    private static object? Read(JsonElement e, SettingKind kind) => kind switch
    {
        SettingKind.Int => e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var i) ? (int)Math.Round(i) : null,
        SettingKind.Decimal => e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var d) ? d : null,
        SettingKind.Bool => e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null,
        _ => e.ValueKind == JsonValueKind.String ? e.GetString() : null
    };

    private static decimal? ToDecimal(object value) => value switch
    {
        decimal d => d,
        int i => i,
        double db => (decimal)db,
        _ => decimal.TryParse(value.ToString()?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)
            ? p
            : null
    };

    private static decimal Clamp(decimal value, SettingDef def)
    {
        if (def.Min is { } min && value < min)
            return min;
        if (def.Max is { } max && value > max)
            return max;
        return value;
    }
}
