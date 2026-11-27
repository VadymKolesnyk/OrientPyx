using System.Globalization;
using System.Text.Json;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>A refusal or a mistake in the call, told to the assistant as is (English, it is not UI text).</summary>
public sealed class McpToolException(string message) : Exception(message);

/// <summary>
/// The arguments of one tool call (a JSON object), read leniently: numbers and booleans may come as strings, a
/// missing optional value is null. A missing required value or a wrong type throws <see cref="McpToolException"/>
/// with a message the assistant can act on.
/// </summary>
public sealed class McpArgs
{
    private readonly JsonElement _root;

    public McpArgs(JsonElement root) =>
        _root = root.ValueKind == JsonValueKind.Object ? root : JsonDocument.Parse("{}").RootElement;

    public static McpArgs Empty { get; } = new(JsonDocument.Parse("{}").RootElement);

    public static McpArgs Parse(string json) => new(JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone());

    public JsonElement Root => _root;

    public bool Has(string name) =>
        _root.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public JsonElement? Get(string name) =>
        _root.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public IEnumerable<string> Names => _root.EnumerateObject().Select(p => p.Name);

    public string? Str(string name) => Get(name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { } v => v.ToString(),
    };

    public string ReqStr(string name) =>
        Str(name) is { } s && s.Trim().Length > 0 ? s : throw new McpToolException($"'{name}' is required.");

    public int? Int(string name)
    {
        if (Get(name) is not { } v)
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9)
            return (int)Math.Round(d);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
            return i;
        throw new McpToolException($"'{name}' must be an integer.");
    }

    public int ReqInt(string name) => Int(name) ?? throw new McpToolException($"'{name}' is required.");

    public decimal? Dec(string name)
    {
        if (Get(name) is not { } v)
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return d;
        throw new McpToolException($"'{name}' must be a number.");
    }

    public double? Num(string name) => Dec(name) is { } d ? (double)d : null;

    public bool? Bool(string name)
    {
        if (Get(name) is not { } v)
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            JsonValueKind.Number when v.TryGetInt32(out var n) => n != 0,
            _ => throw new McpToolException($"'{name}' must be true or false."),
        };
    }

    public bool Flag(string name, bool fallback = false) => Bool(name) ?? fallback;

    public Guid? Id(string name)
    {
        if (Str(name) is not { } s || s.Trim().Length == 0)
            return null;
        return Guid.TryParse(s.Trim(), out var g) ? g : throw new McpToolException($"'{name}' must be an id (GUID) from an earlier answer.");
    }

    public IReadOnlyList<string> Strings(string name)
    {
        if (Get(name) is not { } v)
            return [];
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.ToString())
                .ToList();
        var single = v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
        return single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public IReadOnlyList<McpArgs> Objects(string name)
    {
        if (Get(name) is not { } v)
            return [];
        if (v.ValueKind == JsonValueKind.Object)
            return [new McpArgs(v)];
        if (v.ValueKind != JsonValueKind.Array)
            throw new McpToolException($"'{name}' must be an array of objects.");
        return v.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.Object
                ? new McpArgs(e)
                : throw new McpToolException($"Every item of '{name}' must be an object."))
            .ToList();
    }

    public IReadOnlyList<McpArgs> ReqObjects(string name)
    {
        var list = Objects(name);
        return list.Count > 0 ? list : throw new McpToolException($"'{name}' must be a non-empty array.");
    }

    public McpArgs? Object(string name) =>
        Get(name) is { ValueKind: JsonValueKind.Object } v ? new McpArgs(v) : null;

    public override string ToString() => _root.GetRawText();
}
