using System.Globalization;
using System.Text.Json;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Serialization helpers for a placement-table points rule: an ordered list of point values where
/// index 0 = 1st place. Stored on <see cref="PointsRule.TableJson"/> as a JSON array of decimals.
/// Values are decimals rounded to two fractional digits.
/// </summary>
public static class PointsTable
{
    /// <summary>Parses a stored table JSON into its place values (index 0 = 1st place). Null/blank → empty.</summary>
    public static IReadOnlyList<decimal> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var values = JsonSerializer.Deserialize<List<decimal>>(json);
            return values is null ? [] : values.Select(Normalize).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes place values (index 0 = 1st place) to the stored JSON form, each rounded to 2 dp.</summary>
    public static string Serialize(IEnumerable<decimal> values)
        => JsonSerializer.Serialize(values.Select(Normalize).ToList());

    /// <summary>Rounds a value to two fractional digits (away from zero), the canonical points precision.</summary>
    public static decimal Normalize(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Formats a points value with two fractional digits using the invariant culture.</summary>
    public static string Format(decimal value)
        => Normalize(value).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Parses a user-typed points value (accepts comma or dot); returns 0 on failure.</summary>
    public static decimal ParseValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0m;

        var normalized = text.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Normalize(value)
            : 0m;
    }
}
