using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A saved CSV/Excel column mapping: import target key → the header caption it was mapped from. Keyed by the
/// file's header set (<see cref="HeaderSetHash"/>) so the next file with the same columns is pre-mapped, even when
/// the columns come in another order.
/// </summary>
public static class ImportMapping
{
    /// <summary>A stable hash of the header captions (order-insensitive, compared via <see cref="TextKey"/>).</summary>
    public static string HeaderSetHash(IEnumerable<string> header)
    {
        var keys = header
            .Select(TextKey.Of)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', keys)));
        return Convert.ToHexString(bytes, 0, 16);
    }

    public static string Serialize(IReadOnlyDictionary<string, string> targetToCaption) =>
        JsonSerializer.Serialize(targetToCaption);

    /// <summary>The mapping in <paramref name="json"/>; empty when blank or unreadable.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>The application-default mappings: one JSON object keyed by header-set hash, each value a mapping JSON.</summary>
internal static class ImportMappingDefaults
{
    public static string? Get(string? json, string hash) =>
        Read(json).TryGetValue(hash, out var mapping) ? mapping : null;

    public static string Set(string? json, string hash, string mapping)
    {
        var all = Read(json);
        all[hash] = mapping;
        return JsonSerializer.Serialize(all);
    }

    private static Dictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
