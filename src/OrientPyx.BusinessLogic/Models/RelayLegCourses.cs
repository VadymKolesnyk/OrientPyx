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

    /// <summary>«Своя на кожен етап» (<see cref="Enums.RelayCourseMode.PerLeg"/>): the code of the group's course
    /// variant that holds leg <paramref name="leg"/>'s course — the leg number itself («2»).</summary>
    public static string LegCode(int leg) => leg.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The leg a <see cref="LegCode"/> names, or null for any other code.</summary>
    public static int? LegOfCode(string? code) =>
        int.TryParse(code?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var leg) && leg >= 1 && LegCode(leg) == code!.Trim()
            ? leg
            : null;
}
