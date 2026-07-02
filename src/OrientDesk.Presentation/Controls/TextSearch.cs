using System;
using System.Collections.Generic;
using System.Text;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// Fuzzy, layout-tolerant text matching shared by every search/filter box in the app.
///
/// A user often types a query in the wrong keyboard layout: they meant «Колесник» but the keyboard was
/// still on Latin, so the box actually holds «Rjktcybr». <see cref="Variants"/> turns one raw query into
/// the small set of strings that should all be treated as "the same query":
///
/// 1. the query as typed;
/// 2. the query with every character mapped through the QWERTY↔ЙЦУКЕН keyboard layout, in BOTH
///    directions — so «Rjktc» also matches «Колес» and «Колес» also matches «Rjktc»;
/// 3. if the query contains a Latin <c>s</c> or <c>i</c> (letters a Ukrainian typist reaches for when
///    they want a sound Ukrainian has no dedicated key for), an extra Cyrillic transliteration in which
///    those become the Russian «ы» — e.g. «Bikov» → «Быков» alongside the plain «Біков».
///
/// A cell/label matches the query when it contains ANY of the variants (case-insensitive). Building the
/// variants once per query keeps per-row matching to a handful of ordinal substring checks.
/// </summary>
public static class TextSearch
{
    // US-QWERTY physical key → the character it produces on the standard Windows Ukrainian layout.
    // Only the character-producing keys differ between layouts; punctuation shared by both is left out
    // (it maps to itself). Built as a Latin→Cyrillic table; the reverse table is derived from it so the
    // two directions can never drift apart.
    private static readonly Dictionary<char, char> LatinToCyrillic = BuildLatinToCyrillic();
    private static readonly Dictionary<char, char> CyrillicToLatin = Invert(LatinToCyrillic);

    private static Dictionary<char, char> BuildLatinToCyrillic()
    {
        // Row-by-row against the Ukrainian ЙЦУКЕН layout (lowercase; uppercase handled by casing the
        // whole variant, so only the lowercase mapping is stored).
        const string latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
        const string cyrillic = "йцукенгшщзхїфівапролджєячсмитьбю'";
        var map = new Dictionary<char, char>();
        for (var i = 0; i < latin.Length && i < cyrillic.Length; i++)
            map[latin[i]] = cyrillic[i];
        return map;
    }

    private static Dictionary<char, char> Invert(Dictionary<char, char> source)
    {
        var inverted = new Dictionary<char, char>();
        foreach (var (key, value) in source)
            inverted.TryAdd(value, key); // first-wins; the layout table has no ambiguous reverse keys
        return inverted;
    }

    /// <summary>
    /// All query strings that should be treated as equivalent to <paramref name="query"/>, de-duplicated.
    /// Always contains the original (trimmed) query. Callers match a cell when it contains any variant.
    /// </summary>
    public static IReadOnlyList<string> Variants(string? query)
    {
        var q = query?.Trim() ?? string.Empty;
        if (q.Length == 0)
            return Array.Empty<string>();

        // Preserve order (original first) while de-duplicating so a caller can short-circuit on the
        // first match without re-checking identical strings.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? s)
        {
            if (!string.IsNullOrEmpty(s) && seen.Add(s))
                result.Add(s);
        }

        Add(q);
        Add(MapLayout(q, LatinToCyrillic));
        Add(MapLayout(q, CyrillicToLatin));
        Add(WithSiAsYery(q));

        return result;
    }

    /// <summary>True when <paramref name="text"/> contains any variant of <paramref name="query"/>.</summary>
    public static bool Matches(string? text, string? query)
    {
        if (string.IsNullOrEmpty(query))
            return true;
        var haystack = text ?? string.Empty;
        foreach (var variant in Variants(query))
            if (haystack.Contains(variant, StringComparison.CurrentCultureIgnoreCase))
                return true;
        return false;
    }

    // Re-key a string through a layout table. Characters absent from the table (digits, spaces, shared
    // punctuation) pass through unchanged. Casing is preserved by mapping the lowercase form and
    // restoring the original character's case. Returns the mapped string, or null when nothing changed
    // (so it collapses away in de-duplication rather than duplicating the original).
    private static string? MapLayout(string s, Dictionary<char, char> table)
    {
        var sb = new StringBuilder(s.Length);
        var changed = false;
        foreach (var ch in s)
        {
            var lower = char.ToLowerInvariant(ch);
            if (table.TryGetValue(lower, out var mapped))
            {
                sb.Append(char.IsUpper(ch) ? char.ToUpperInvariant(mapped) : mapped);
                changed = true;
            }
            else
            {
                sb.Append(ch);
            }
        }
        return changed ? sb.ToString() : null;
    }

    // When the query has a Latin s or i, produce a Cyrillic reading in which those become «ы» (and the
    // rest is transliterated to the nearest Cyrillic letter). This lets a typist searching for a name
    // they'd spell «Быков» find it by typing «Bykov»/«Bikov»/«Bskov». Returns null when there's no s/i to
    // key off, so the variant only appears when it's meaningful.
    private static string? WithSiAsYery(string s)
    {
        var hasTrigger = false;
        foreach (var ch in s)
            if (ch is 's' or 'S' or 'i' or 'I')
            {
                hasTrigger = true;
                break;
            }
        if (!hasTrigger)
            return null;

        var sb = new StringBuilder(s.Length);
        var changed = false;
        foreach (var ch in s)
        {
            var lower = char.ToLowerInvariant(ch);
            char? mapped = lower switch
            {
                's' or 'i' => 'ы',
                _ => Translit.TryGetValue(lower, out var m) ? m : null
            };
            if (mapped is { } c)
            {
                sb.Append(char.IsUpper(ch) ? char.ToUpperInvariant(c) : c);
                changed = true;
            }
            else
            {
                sb.Append(ch);
            }
        }
        return changed ? sb.ToString() : null;
    }

    // Minimal single-char Latin→Cyrillic transliteration used only to fill in the non-s/i letters of the
    // «ы» variant. Deliberately covers just the unambiguous 1:1 letters; multi-letter clusters (sh, ch…)
    // are out of scope for this heuristic.
    private static readonly Dictionary<char, char> Translit = new()
    {
        ['a'] = 'а', ['b'] = 'б', ['v'] = 'в', ['g'] = 'г', ['d'] = 'д', ['e'] = 'е', ['z'] = 'з',
        ['k'] = 'к', ['l'] = 'л', ['m'] = 'м', ['n'] = 'н', ['o'] = 'о', ['p'] = 'п', ['r'] = 'р',
        ['t'] = 'т', ['u'] = 'у', ['f'] = 'ф', ['h'] = 'х', ['c'] = 'ц', ['y'] = 'й'
    };
}
