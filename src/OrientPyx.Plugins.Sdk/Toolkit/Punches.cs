namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>Helpers over punch / course-order code strings.</summary>
public static class Punches
{
    /// <summary>Splits a whitespace-separated code string into trimmed, non-empty codes.</summary>
    public static IEnumerable<string> Split(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Drops the start/finish box codes from a code list.</summary>
    public static IEnumerable<string> WithoutStartFinish(IEnumerable<string> codes, IReadOnlySet<string> startFinishCodes) =>
        codes.Where(c => !startFinishCodes.Contains(c.Trim()));

    /// <summary>Drops the disabled («проблемні») control codes from a code list.</summary>
    public static IEnumerable<string> WithoutDisabled(IEnumerable<string> codes, IReadOnlySet<string> disabledCodes) =>
        codes.Where(c => !disabledCodes.Contains(c.Trim()));
}
