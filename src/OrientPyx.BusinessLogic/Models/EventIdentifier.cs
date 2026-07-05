namespace OrientPyx.BusinessLogic.Models;

/// <summary>Rules for a competition identifier — the folder name under the events path.</summary>
public static class EventIdentifier
{
    /// <summary>
    /// True when <paramref name="identifier"/> can be used as a competition folder name: non-blank, no
    /// path-illegal characters, no separators, and not a navigation token ("." / "..").
    /// </summary>
    public static bool IsValid(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        if (identifier.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        return identifier is not "." and not ".."
            && !identifier.Contains(Path.DirectorySeparatorChar)
            && !identifier.Contains(Path.AltDirectorySeparatorChar);
    }
}
