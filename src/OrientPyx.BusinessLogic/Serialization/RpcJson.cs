using System.Text.Json;
using System.Text.Json.Serialization;
using OrientPyx.BusinessLogic.Disciplines;

namespace OrientPyx.BusinessLogic.Serialization;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> every network call uses, on the host and on a station alike.
/// <list type="bullet">
/// <item><see cref="JsonSerializerOptions.IncludeFields"/> — three <c>IEventStore</c> batch methods take lists of
/// <c>ValueTuple</c>s, whose elements are fields (<c>Item1</c>…); without this they would serialise as <c>{}</c>.
/// The models and entities have no public fields (a contract test keeps it so).</item>
/// <item><see cref="DisciplineIdJsonConverter"/> — <see cref="DisciplineId"/> is a struct with a private field and no
/// settable property, so it needs an explicit string form.</item>
/// <item>Enums as strings — stable across a re-ordered enum between app versions.</item>
/// </list>
/// </summary>
public static class RpcJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new DisciplineIdJsonConverter());
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Reads/writes a <see cref="DisciplineId"/> as its stored string (<c>"SetCourse"</c>, <c>"plugin.name"</c>).</summary>
public sealed class DisciplineIdJsonConverter : JsonConverter<DisciplineId>
{
    public override DisciplineId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, DisciplineId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}

/// <summary>
/// <see cref="IReadOnlySet{T}"/> properties (e.g. <c>ParticipantImportScope.ExcludedRows</c>): System.Text.Json
/// serialises the interface but refuses to deserialise into it — this reads such a value into a <see cref="HashSet{T}"/>.
/// </summary>
public sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<IReadOnlySet<T>>
    {
        public override IReadOnlySet<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<HashSet<T>>(ref reader, options);

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<T> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, (IEnumerable<T>)value, options);
    }
}
