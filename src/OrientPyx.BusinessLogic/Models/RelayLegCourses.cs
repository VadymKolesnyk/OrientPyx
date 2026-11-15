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

    /// <summary>A leg number as text («2») — how a «своя на кожен етап» leg without a course is reported, and the code
    /// a course on that leg needs no mark for (courses typed before the «Етап» column were coded so).</summary>
    public static string LegCode(int leg) => leg.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
