using System.Text.RegularExpressions;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Links into the documentation site (https://vadymkolesnyk.github.io/OrientPyx/). Its «Довідник екранів» pages are
/// generated from the same <c>Help.*</c> keys the «?» dialogs show (<c>website/hooks.py</c>), one page per screen and a
/// section per block: <c>Help.EntryFees</c> → <c>screens/entry-fees/</c>, <c>Help.EntryFees.Chips</c> →
/// <c>screens/entry-fees/#chips</c>. Keep this rule and the hook's <c>kebab</c> in step.
/// </summary>
public static partial class DocsLinks
{
    public const string Site = "https://vadymkolesnyk.github.io/OrientPyx/";

    public static Uri Home { get; } = new(Site);

    /// <summary>The documentation page of a help key prefix (<c>Help.&lt;Screen&gt;[.&lt;Block&gt;]</c>); the site's home otherwise.</summary>
    public static Uri ForHelp(string? keyPrefix)
    {
        var parts = (keyPrefix ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "Help")
            return Home;
        var url = $"{Site}screens/{Kebab(parts[1])}/";
        if (parts.Length > 2)
            url += "#" + Kebab(parts[2]);
        return new Uri(url);
    }

    /// <summary><c>CompetitionInfo</c> → <c>competition-info</c>.</summary>
    public static string Kebab(string name) => WordStart().Replace(name, "-").ToLowerInvariant();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordStart();
}
