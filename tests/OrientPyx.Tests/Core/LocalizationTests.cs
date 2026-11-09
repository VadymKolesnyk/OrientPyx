using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>The two resource dictionaries stay in step, and the plugin overlay falls back the documented way.</summary>
[Collection(AppCollection.Name)]
public sealed class LocalizationTests
{
    private static Dictionary<string, string> Load(string culture)
    {
        var path = Path.Combine(TestEnvironment.RepoRoot, "src", "OrientPyx.Localization", "Resources", $"{culture}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    [Fact]
    public void UkrainianAndEnglishHaveTheSameKeys()
    {
        var uk = Load("uk-UA");
        var en = Load("en-US");
        var missingInEn = uk.Keys.Except(en.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missingInUk = en.Keys.Except(uk.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(missingInEn.Count == 0, "Keys missing in en-US: " + string.Join(", ", missingInEn));
        Assert.True(missingInUk.Count == 0, "Keys missing in uk-UA: " + string.Join(", ", missingInUk));
        var blank = uk.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).Except(["EventSelection.Col.Hidden"]).ToList();
        Assert.True(blank.Count == 0, "Blank uk-UA values: " + string.Join(", ", blank));
        Assert.True(uk.Count > 500);
    }

    [Fact]
    public void FormatPlaceholdersMatchBetweenLanguages()
    {
        var uk = Load("uk-UA");
        var en = Load("en-US");
        foreach (var (key, ukText) in uk)
        {
            if (!en.TryGetValue(key, out var enText))
                continue;
            var ukHoles = Enumerable.Range(0, 5).Where(i => ukText.Contains($"{{{i}}}")).ToList();
            var enHoles = Enumerable.Range(0, 5).Where(i => enText.Contains($"{{{i}}}")).ToList();
            Assert.True(ukHoles.SequenceEqual(enHoles), $"placeholders differ for {key}: uk {{{string.Join(",", ukHoles)}}} vs en {{{string.Join(",", enHoles)}}}");
        }
    }

    private static readonly string[] HelpSections = ["Title", "What", "Why", "How"];

    // A «?» with no text behind it shows the bare keys in the dialog (Monitor / Splits / SummaryProtocol once did:
    // their view-model names differ from the help prefix).
    [Fact]
    public void EveryHelpButtonHasItsText()
    {
        var uk = Load("uk-UA");
        var en = Load("en-US");
        var missing = new List<string>();
        foreach (var prefix in PageHelpPrefixes().Concat(BlockHelpPrefixes()).Distinct())
        {
            foreach (var section in HelpSections)
            {
                var key = $"{prefix}.{section}";
                if (!uk.TryGetValue(key, out var ukText) || string.IsNullOrWhiteSpace(ukText))
                    missing.Add($"uk-UA {key}");
                if (!en.TryGetValue(key, out var enText) || string.IsNullOrWhiteSpace(enText))
                    missing.Add($"en-US {key}");
            }
        }
        Assert.True(missing.Count == 0, "Help text missing: " + string.Join(", ", missing));
    }

    // The other way round: help text that no «?» opens is a renamed page or a removed block.
    [Fact]
    public void EveryHelpTextHasAButton()
    {
        var used = PageHelpPrefixes().Concat(BlockHelpPrefixes()).ToHashSet(StringComparer.Ordinal);
        var orphans = Load("uk-UA").Keys
            .Where(k => k.StartsWith("Help.", StringComparison.Ordinal) && k.EndsWith(".Title", StringComparison.Ordinal))
            .Select(k => k[..^".Title".Length])
            .Where(p => !used.Contains(p))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(orphans.Count == 0, "Help text that no «?» opens: " + string.Join(", ", orphans));
    }

    // Header «?»: every page view model (PageView in the shell, the Settings and Network overlays) except About, whose
    // overlay has none. HelpKeyPrefix is read off an uninitialised instance: the base derives it from the type name and
    // the overrides return a constant.
    private static IEnumerable<string> PageHelpPrefixes() =>
        typeof(PageViewModelBase).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(PageViewModelBase)) && t != typeof(AboutViewModel))
            .Select(t => ((PageViewModelBase)RuntimeHelpers.GetUninitializedObject(t)).HelpKeyPrefix);

    // Block «?»: ShowBlockHelpCommand with the block's prefix as the CommandParameter.
    private static IEnumerable<string> BlockHelpPrefixes() =>
        Directory.EnumerateFiles(Path.Combine(TestEnvironment.RepoRoot, "src", "OrientPyx.Presentation"), "*.axaml",
                SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "CommandParameter=\"(Help\\.[A-Za-z.]+)\""))
            .Select(m => m.Groups[1].Value);

    [Fact]
    public void ServiceResolvesSwitchesAndOverlays()
    {
        var loc = TestEnvironment.Instance.Services.GetRequiredService<ILocalizationService>();
        var uk = loc.AvailableCultures.Single(c => c.Name == "uk-UA");
        var en = loc.AvailableCultures.Single(c => c.Name == "en-US");
        loc.SetLanguage(uk);
        try
        {
            Assert.Equal("Учасники", loc.Get("Nav.Participants"));
            Assert.Equal("Учасники", loc["Nav.Participants"]);
            Assert.Equal("No.Such.Key", loc.Get("No.Such.Key"));
            Assert.Equal("", loc.Get(""));

            loc.AddOverlay("en-US", "tp", new Dictionary<string, string> { ["hello"] = "Hello", ["only"] = "Only in English" });
            loc.AddOverlay("uk-UA", "tp", new Dictionary<string, string> { ["hello"] = "Привіт" });
            Assert.Equal("Привіт", loc.Get("tp.hello"));
            Assert.Equal("Only in English", loc.Get("tp.only"));       // falls back to en-US
            Assert.Equal("none", loc.Get("tp.none"));                  // then to the bare key without the prefix

            loc.SetLanguage(en);
            Assert.Equal("Participants", loc.Get("Nav.Participants"));
            Assert.Equal("Hello", loc.Get("tp.hello"));
            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            loc.SetLanguage(uk);
        }
    }
}
