using System.Text.Json.Nodes;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>A tiny builder of the JSON schemas of the tool arguments (keeps the catalogue readable).</summary>
internal static class McpSchema
{
    public readonly record struct Prop(string Name, JsonObject Schema, bool Required);

    public static Prop Req(string name, JsonObject schema) => new(name, schema, true);

    public static Prop Opt(string name, JsonObject schema) => new(name, schema, false);

    public static JsonObject Obj(params Prop[] props) => Obj(null, props);

    public static JsonObject Obj(string? description, params Prop[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = p.Schema;
            if (p.Required)
                required.Add(p.Name);
        }
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            o["required"] = required;
        if (description is not null)
            o["description"] = description;
        return o;
    }

    /// <summary>An object with free keys (a patch, a field map).</summary>
    public static JsonObject Map(string description) =>
        new() { ["type"] = "object", ["description"] = description, ["additionalProperties"] = true };

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Enum(string description, params string[] values)
    {
        var e = new JsonArray();
        foreach (var v in values)
            e.Add(v);
        return new JsonObject { ["type"] = "string", ["enum"] = e, ["description"] = description };
    }

    public static JsonObject Arr(JsonObject items, string description) =>
        new() { ["type"] = "array", ["items"] = items, ["description"] = description };

    public static JsonObject StrArr(string description) => Arr(new JsonObject { ["type"] = "string" }, description);

    public static JsonObject IntArr(string description) => Arr(new JsonObject { ["type"] = "integer" }, description);

    // ── Shared arguments

    public static Prop Competition => Req("competition",
        Str("Competition identifier from get_context. The call is refused when another competition is open in the window."));

    public static Prop Day => Opt("day", Int("Day number (1-based). Default: the day selected in the window."));

    public static Prop Preview => Opt("preview",
        Bool("true (default): nothing changes, the answer shows what would change and a plan_id for apply_plan."));
}
