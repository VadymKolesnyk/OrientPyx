using System.Globalization;
using System.Text.Json;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Reads and writes the relay course settings of a group (Р-41): the «leg → course code» map of
/// <see cref="Enums.RelayCourseMode.ByLeg"/> (<c>GroupDaySettings.LegCoursesJson</c>) and a «набір на команду» course
/// set («A B B», codes separated by spaces or commas). A malformed value reads as empty.
/// </summary>
public static class RelayLegCourses
{
    public static IReadOnlyDictionary<int, string> Parse(string? json)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (raw is null)
                return result;
            foreach (var (legText, code) in raw)
                if (int.TryParse(legText, NumberStyles.None, CultureInfo.InvariantCulture, out var leg) && leg >= 1
                    && !string.IsNullOrWhiteSpace(code))
                    result[leg] = code.Trim();
        }
        catch (JsonException)
        {
        }
        return result;
    }

    public static string Format(IReadOnlyDictionary<int, string> courses)
    {
        var raw = courses
            .Where(kv => kv.Key >= 1 && !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key)
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value.Trim());
        return raw.Count == 0 ? string.Empty : JsonSerializer.Serialize(raw);
    }

    /// <summary>The course codes of a set, in order, duplicates kept («A B B» → A, B, B).</summary>
    public static IReadOnlyList<string> ParseSet(string? set) =>
        string.IsNullOrWhiteSpace(set)
            ? []
            : set.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A course set written back the way <see cref="ParseSet"/> reads it.</summary>
    public static string FormatSet(IEnumerable<string> codes) => string.Join(' ', codes.Select(c => c.Trim()).Where(c => c.Length > 0));
}
