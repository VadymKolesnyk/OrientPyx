using System.Text;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The one way user-entered names (groups, regions, clubs, teams, ranks, chip/bib numbers …) are matched.
///
/// Two strings are "the same name" when they LOOK the same, so <see cref="Of"/> folds away everything a person
/// can't see:
/// 1. surrounding whitespace, and runs of inner whitespace (incl. non-breaking space) collapse to one space;
/// 2. invisible characters (zero-width space/joiners, BOM, soft hyphen) are dropped;
/// 3. case — the key is upper-case;
/// 4. Cyrillic letters that are visually identical to Latin ones (homoglyphs) become the Latin letter, so
///    «М12» (Cyrillic М) = «M12» and «ОРЕN» (Cyrillic О, Р, Е) = «OPEN». A typist switching layouts mid-word
///    produces exactly these mixes, and the strings then look equal on screen while comparing different.
///
/// The key is for MATCHING only — stored/displayed text is never replaced by it.
/// </summary>
public static class TextKey
{
    /// <summary>Equality comparer over <see cref="Of"/> keys (null compares as empty).</summary>
    public static IEqualityComparer<string> Comparer { get; } = new KeyComparer();

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> have the same key.</summary>
    public static bool Same(string? a, string? b) => string.Equals(Of(a), Of(b), StringComparison.Ordinal);

    /// <summary>The canonical matching key of <paramref name="text"/>; empty for null/blank.</summary>
    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (IsInvisible(ch))
                continue;
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0; // leading whitespace is dropped, inner runs collapse to one
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(Fold(char.ToUpperInvariant(ch)));
        }
        return sb.ToString();
    }

    private static bool IsInvisible(char ch) => ch is
        '​' or '‌' or '‍' or '⁠' or '﻿' or '­';

    // Upper-case Cyrillic letters that render the same as a Latin capital. (Lower-case pairs like а/a, о/o, р/p,
    // с/c, е/e, х/x, у/y, і/i reach here already upper-cased.)
    private static char Fold(char ch) => ch switch
    {
        'А' => 'A',
        'В' => 'B',
        'Е' => 'E',
        'К' => 'K',
        'М' => 'M',
        'Н' => 'H',
        'О' => 'O',
        'Р' => 'P',
        'С' => 'C',
        'Т' => 'T',
        'Х' => 'X',
        'У' => 'Y',
        'І' => 'I',
        'Ѕ' => 'S',
        'Ј' => 'J',
        _ => ch
    };

    private sealed class KeyComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => Same(x, y);

        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Of(obj));
    }
}
