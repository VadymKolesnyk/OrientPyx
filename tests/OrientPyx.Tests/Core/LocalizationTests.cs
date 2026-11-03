using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.Localization;
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
