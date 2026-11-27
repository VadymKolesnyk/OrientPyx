using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OrientPyx.BusinessLogic.Serialization;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>
/// The JSON the assistant receives: snake_case names (the same as the analytics tables), enums as lower-case words,
/// finish statuses as the protocol marks (OK / MP / DNF …), times as seconds with full precision, nulls left out,
/// Cyrillic unescaped (readable and fewer tokens).
/// </summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            IncludeFields = true,
            MaxDepth = 48,
        };
        options.Converters.Add(new FinishStatusJsonConverter());
        options.Converters.Add(new SecondsTimeSpanConverter());
        options.Converters.Add(new DisciplineIdJsonConverter());
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>Any value as a JSON node in the assistant's format.</summary>
    public static JsonNode? ToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), Options);

    /// <summary>Any value as compact JSON text in the assistant's format.</summary>
    public static string Serialize(object? value) =>
        value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), Options);

    /// <summary>The protocol mark of a status: OK, MP, OVT, DNF, DNS, DSQ; <c>none</c> = no result yet.</summary>
    public static string StatusText(FinishStatus status) => status switch
    {
        FinishStatus.None => "none",
        _ => status.ToString().ToUpperInvariant(),
    };

    /// <summary>Parses a status mark (any case; «none»/blank = clear).</summary>
    public static bool TryParseStatus(string? text, out FinishStatus? status)
    {
        status = null;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("null", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Enum.TryParse<FinishStatus>(t, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            status = parsed == FinishStatus.None ? null : parsed;
            return true;
        }
        return false;
    }

    private sealed class FinishStatusJsonConverter : JsonConverter<FinishStatus>
    {
        public override FinishStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TryParseStatus(reader.GetString(), out var s) ? s ?? FinishStatus.None : FinishStatus.None;

        public override void Write(Utf8JsonWriter writer, FinishStatus value, JsonSerializerOptions options) =>
            writer.WriteStringValue(StatusText(value));
    }

    private sealed class SecondsTimeSpanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number
                ? TimeSpan.FromSeconds(reader.GetDouble())
                : McpTime.TryParse(reader.GetString(), out var t) ? t : TimeSpan.Zero;

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Math.Round(value.TotalSeconds, 3));
    }
}

/// <summary>Time-of-day / duration text the assistant may send: «10:30», «10:30:15», «1:02:03.5», or seconds.</summary>
public static class McpTime
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
            return false;
        if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return seconds >= 0;
        }
        var parts = t.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || h < 0 || m is < 0 or > 59)
            return false;
        var s = 0d;
        if (parts.Length == 3 && !double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out s))
            return false;
        if (s is < 0 or >= 60)
            return false;
        value = new TimeSpan(h, m, 0) + TimeSpan.FromSeconds(s);
        return true;
    }

    /// <summary>A time of day as «HH:mm:ss» (fractions dropped, never rounded — as every output shows it).</summary>
    public static string Clock(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
