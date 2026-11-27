using System.Globalization;
using System.Text.Json;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>Values the assistant sends, read the way the program's own cells read them.</summary>
internal static class McpValues
{
    /// <summary>A date: YYYY-MM-DD, DD.MM.YYYY or a bare year (1 January). Null/blank = clear.</summary>
    public static DateTimeOffset? Date(string? text, string field)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0 || t.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;
        string[] formats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "yyyy.MM.dd", "dd/MM/yyyy"];
        if (DateOnly.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (t.Length == 4 && int.TryParse(t, out var year) && year is > 1900 and < 2100)
            return new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero);
        throw new McpToolException($"'{field}': '{t}' is not a date (YYYY-MM-DD).");
    }

    /// <summary>A start time of the day: HH:mm[:ss] or seconds. Null/blank = clear.</summary>
    public static TimeSpan? Time(string? text, string field)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0 || t.Equals("null", StringComparison.OrdinalIgnoreCase))
            return null;
        return McpTime.TryParse(t, out var v) && v < TimeSpan.FromHours(48)
            ? v
            : throw new McpToolException($"'{field}': '{t}' is not a time (HH:mm:ss).");
    }

    /// <summary>A field of a field map as text (numbers and booleans as their JSON text); null for JSON null.</summary>
    public static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.ToString(),
    };

    public static bool Bool(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
        JsonValueKind.Number => value.GetDouble() != 0,
        _ => throw new McpToolException($"'{field}' must be true or false."),
    };

    public static int? Int(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number when value.TryGetInt32(out var i) => i,
        JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()) => null,
        JsonValueKind.String when int.TryParse(value.GetString(), out var i) => i,
        _ => throw new McpToolException($"'{field}' must be an integer."),
    };

    public static decimal? Dec(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.String when string.IsNullOrWhiteSpace(value.GetString()) => null,
        JsonValueKind.String when decimal.TryParse(value.GetString()!.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => throw new McpToolException($"'{field}' must be a number."),
    };

    public static string Show(object? value) => value switch
    {
        null => "—",
        bool b => b ? "так" : "ні",
        DateTimeOffset d => d.ToString("yyyy-MM-dd"),
        TimeSpan t => McpTime.Clock(t),
        string s when s.Trim().Length == 0 => "—",
        _ => value.ToString() ?? "—",
    };
}
