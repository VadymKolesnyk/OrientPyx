using System.Runtime.CompilerServices;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The plugin API reference on the documentation site (<c>website/content/plugins/api.md</c>) names every public type
/// of the plugin SDK, so a type added to the SDK cannot stay undocumented.
/// </summary>
public sealed class PluginDocsTests
{
    [Fact]
    public void ApiReferenceMentionsEveryPublicSdkType()
    {
        var path = Path.Combine(TestEnvironment.RepoRoot, "website", "content", "plugins", "api.md");
        Assert.True(File.Exists(path), "The plugin API reference is missing: " + path);
        var text = File.ReadAllText(path);

        var types = typeof(OrientPyx.Plugins.Sdk.IOrientPyxPlugin).Assembly.GetExportedTypes()
            .Where(t => !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) && !t.Name.Contains('<'))
            .ToList();
        Assert.NotEmpty(types);

        var missing = types
            .Where(t => !IsMentioned(text, t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"website/content/plugins/api.md does not mention {missing.Count} public SDK type(s) in backticks "
            + "(write `Name`, or `Name<` for a generic type): " + string.Join(", ", missing));
    }

    // `Name` or `Name<…>`; a nested type may also be written with its declaring types: `SiCard.Info`.
    private static bool IsMentioned(string text, Type type)
    {
        if (Contains(text, SimpleName(type)))
            return true;

        var qualified = SimpleName(type);
        for (var outer = type.DeclaringType; outer is not null; outer = outer.DeclaringType)
        {
            qualified = SimpleName(outer) + "." + qualified;
            if (Contains(text, qualified))
                return true;
        }
        return false;
    }

    private static bool Contains(string text, string name) =>
        text.Contains("`" + name + "`", StringComparison.Ordinal)
        || text.Contains("`" + name + "<", StringComparison.Ordinal);

    // The name as written in C#: no generic arity suffix ("Foo`1" → "Foo").
    private static string SimpleName(Type type)
    {
        var tick = type.Name.IndexOf('`');
        return tick < 0 ? type.Name : type.Name[..tick];
    }
}
