using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ICourseNameSplitter"/>. Ported from the CourseParser web tool: split the name
/// on the delimiters <c>; , .</c>, normalize Latin aliases to their Ukrainian letters, then expand
/// any token whose first two characters are both group prefixes glued in front of a digit
/// (e.g. "ЧЖ55" → "Ч55", "Ж55").
/// </summary>
public sealed class CourseNameSplitter : ICourseNameSplitter
{
    // Characters that act as a single-letter group prefix. Includes the Latin aliases (W, M, E, A)
    // defensively; after normalization a token only ever contains the Cyrillic forms, but mirroring
    // the original set keeps the behaviour identical.
    private const string PrefixChars = "ЧЖМЕАWMEA";

    private static readonly char[] Delimiters = [';', ',', '.'];

    public IReadOnlyList<string> Split(string courseName)
    {
        if (string.IsNullOrWhiteSpace(courseName))
            return [];

        var result = new List<string>();
        foreach (var token in courseName.Split(Delimiters))
        {
            var normalized = Normalize(token);
            if (normalized.Length == 0)
                continue;
            result.AddRange(ExpandCombinedPrefix(normalized));
        }

        return result;
    }

    // Map Latin aliases to the Ukrainian letters used by the groups (W→Ж, M/М→Ч, E→Е, A→А), then
    // trim. Order matches the original tool: Latin M and Cyrillic М both collapse to Ч.
    private static string Normalize(string token) =>
        token
            .Replace("W", "Ж")
            .Replace("M", "Ч")
            .Replace("М", "Ч")
            .Replace("E", "Е")
            .Replace("A", "А")
            .Trim();

    // "ЧЖ55" → ["Ч55", "Ж55"] when the first two chars are both prefixes and the third is a digit;
    // otherwise the token is returned unchanged.
    private static IReadOnlyList<string> ExpandCombinedPrefix(string token)
    {
        if (token.Length < 3)
            return [token];

        var c0 = token[0];
        var c1 = token[1];
        if (PrefixChars.Contains(c0) && PrefixChars.Contains(c1))
        {
            var rest = token[2..];
            if (rest.Length > 0 && char.IsDigit(rest[0]))
                return [c0 + rest, c1 + rest];
        }

        return [token];
    }
}
