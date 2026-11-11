namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Reads and writes a relay «набір на команду» course set (Р-41): course codes separated by spaces or commas
/// («A B B»).
/// </summary>
public static class RelayLegCourses
{
    /// <summary>The course codes of a set, in order, duplicates kept («A B B» → A, B, B).</summary>
    public static IReadOnlyList<string> ParseSet(string? set) =>
        string.IsNullOrWhiteSpace(set)
            ? []
            : set.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A course set written back the way <see cref="ParseSet"/> reads it.</summary>
    public static string FormatSet(IEnumerable<string> codes) => string.Join(' ', codes.Select(c => c.Trim()).Where(c => c.Length > 0));
}
