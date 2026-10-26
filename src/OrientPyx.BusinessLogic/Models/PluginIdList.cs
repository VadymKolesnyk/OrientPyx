using System.Text.Json;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>The stored form of a set of plugin ids (a JSON string array). Unreadable text reads as empty.</summary>
public static class PluginIdList
{
    public static IReadOnlySet<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return new HashSet<string>(
                (JsonSerializer.Deserialize<string[]>(json) ?? []).Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static string Serialize(IEnumerable<string> ids) =>
        JsonSerializer.Serialize(ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
}
