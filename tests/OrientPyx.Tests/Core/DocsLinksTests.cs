using System.Text.Json;
using System.Text.RegularExpressions;
using OrientPyx.Presentation.Services;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The «Детальніше в документації» link of every «?» dialog points at a page the documentation site generates
/// (<c>website/hooks.py</c> builds one «Довідник екранів» page per <c>Help.&lt;Screen&gt;.Title</c> and a section per
/// <c>Help.&lt;Screen&gt;.&lt;Block&gt;.Title</c>, with the same kebab-case rule).
/// </summary>
public sealed class DocsLinksTests
{
    [Fact]
    public void ScreenAndBlockPrefixesMapToTheirPages()
    {
        Assert.Equal("https://vadymkolesnyk.github.io/OrientPyx/screens/competition-info/", DocsLinks.ForHelp("Help.CompetitionInfo").AbsoluteUri);
        Assert.Equal("https://vadymkolesnyk.github.io/OrientPyx/screens/entry-fees/#chips", DocsLinks.ForHelp("Help.EntryFees.Chips").AbsoluteUri);
        Assert.Equal(DocsLinks.Home, DocsLinks.ForHelp("Common.Close"));
        Assert.Equal(DocsLinks.Home, DocsLinks.ForHelp(null));
    }

    [Fact]
    public void TheHookUsesTheSameRule()
    {
        var hook = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, "website", "hooks.py"));
        Assert.Contains("re.sub(r\"(?<=[a-z0-9])(?=[A-Z])\", \"-\", name).lower()", hook);
        Assert.Contains("f\"screens/{kebab(screen)}.md\"", hook);
        Assert.Contains("{{#{kebab(block)}}}", hook);

        // Every screen the hook will generate has a link the app can build.
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(TestEnvironment.RepoRoot, "src", "OrientPyx.Localization", "Resources", "uk-UA.json")))!;
        foreach (var key in strings.Keys.Where(k => Regex.IsMatch(k, @"^Help\.[A-Za-z]+(\.[A-Za-z]+)?\.Title$")))
        {
            var uri = DocsLinks.ForHelp(key[..^".Title".Length]);
            Assert.StartsWith(DocsLinks.Site + "screens/", uri.AbsoluteUri);
        }
    }
}
