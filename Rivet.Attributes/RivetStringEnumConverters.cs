using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet;

/// <summary>
/// Lowercases the exact member name to the whole word — "DarkGreen" →
/// "darkgreen". System.Text.Json ships no lowercase-all naming policy, so the
/// policy is derived here; <see cref="JsonNamingPolicy"/> is a non-sealed class
/// with a protected constructor and a virtual <see cref="JsonNamingPolicy.ConvertName"/>,
/// which keeps the entire enum-string machinery (pins, flags, round-trip) native.
/// </summary>
internal sealed class RivetLowerAllNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name.ToLowerInvariant();
}

/// <summary>
/// String-wire enum converter whose naming policy is the type itself: the
/// converter a source enum declares in [JsonConverter(typeof(...))] names both
/// the string wire and the casing convention — one declaration per enum, no
/// second attribute to keep in sync, no ambient configuration.
///
/// A member-level [JsonStringEnumMemberName("original")] always wins over the
/// policy; a bare [JsonConverter(typeof(JsonStringEnumConverter<...>))] (the
/// built-in, no policy) keeps the exact C# member names. The contract tool
/// derives the same wire values from the same converter type name, so the
/// emitted string union matches the runtime wire by construction.
/// </summary>
/// <remarks>
/// The declared failures are refusal, not fallback: the contract tool refuses a
/// policy casing that collides two members into one wire value (RIV1106), and
/// never silently falls back to numeric when a policy is declared.
///
/// Writing is System.Text.Json's own <see cref="JsonStringEnumConverter{TEnum}"/>
/// with the policy. Reading accepts exactly the names it writes, case-sensitively,
/// on every target framework: an integer, a numeric string such as <c>"0"</c>, the C#
/// member name in another casing, or a combination of flags is a
/// <see cref="JsonException"/>. Dictionary keys follow the same rule. The built-in
/// <see cref="JsonStringEnumConverter{TEnum}"/> keeps STJ's leniency.
///
/// None of this applies to route, query, header or form values. ASP.NET binds
/// those itself and never consults a JSON converter.
/// </remarks>
public abstract class RivetStringEnumConverter<T> : JsonConverterFactory
    where T : struct, Enum
{
    private readonly JsonNamingPolicy _policy;

    private protected RivetStringEnumConverter(JsonNamingPolicy policy) => _policy = policy;

    public sealed override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(T);

    public sealed override JsonConverter CreateConverter(
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        new RivetExactEnumConverter<T>(
            (JsonConverter<T>)
                new JsonStringEnumConverter<T>(_policy, allowIntegerValues: false).CreateConverter(
                    typeToConvert,
                    options
                )!,
            options
        );
}

/// <summary>
/// Writes through System.Text.Json's enum converter and reads back only what that writes.
/// The accepted names are taken from the writer itself, so policies, member pins and
/// dictionary keys can't drift from the read side.
/// </summary>
internal sealed class RivetExactEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private readonly JsonConverter<T> _writer;
    private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, T> _keys = new(StringComparer.Ordinal);

    internal RivetExactEnumConverter(JsonConverter<T> writer, JsonSerializerOptions options)
    {
        _writer = writer;
        foreach (var value in Enum.GetValues<T>())
        {
            _values.TryAdd(WrittenValue(value, options), value);
            _keys.TryAdd(WrittenKey(value, options), value);
        }
    }

    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType == JsonTokenType.String
        && _values.TryGetValue(reader.GetString()!, out var value)
            ? value
            : throw Refuse(ref reader, _values.Keys);

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        _writer.Write(writer, value, options);

    public override T ReadAsPropertyName(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        _keys.TryGetValue(reader.GetString()!, out var value)
            ? value
            : throw Refuse(ref reader, _keys.Keys);

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer,
        T value,
        JsonSerializerOptions options
    ) => _writer.WriteAsPropertyName(writer, value, options);

    private static JsonException Refuse(ref Utf8JsonReader reader, IEnumerable<string> accepted)
    {
        var sent = reader.TokenType switch
        {
            JsonTokenType.String or JsonTokenType.PropertyName => $"\"{reader.GetString()}\"",
            JsonTokenType.StartObject => "A JSON object",
            JsonTokenType.StartArray => "A JSON array",
            JsonTokenType.True or JsonTokenType.False => "A JSON boolean",
            JsonTokenType.Number => "A JSON number",
            JsonTokenType.Null => "A JSON null",
            _ => "A JSON value",
        };
        return new JsonException(
            $"{sent} is not a {typeof(T).Name} value. Send one of "
                + string.Join(", ", accepted.Select(name => $"\"{name}\""))
                + ", exactly as written."
        );
    }

    private string WrittenValue(T value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            _writer.Write(json, value, options);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        reader.Read();
        return reader.GetString()!;
    }

    private string WrittenKey(T value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            _writer.WriteAsPropertyName(json, value, options);
            json.WriteNullValue();
            json.WriteEndObject();
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        reader.Read();
        reader.Read();
        return reader.GetString()!;
    }
}

/// <summary>lowercase enum wire values — "DarkGreen" → "darkgreen".</summary>
public sealed class RivetLowerCaseEnumConverter<T>()
    : RivetStringEnumConverter<T>(new RivetLowerAllNamingPolicy())
    where T : struct, Enum;

/// <summary>camelCase enum wire values — "DarkGreen" → "darkGreen".</summary>
public sealed class RivetCamelCaseEnumConverter<T>()
    : RivetStringEnumConverter<T>(JsonNamingPolicy.CamelCase)
    where T : struct, Enum;

/// <summary>snake_case enum wire values — "RestrictedByway2A" → "restricted_byway2_a".</summary>
public sealed class RivetSnakeCaseEnumConverter<T>()
    : RivetStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower)
    where T : struct, Enum;

/// <summary>kebab-case enum wire values — "RestrictedByway2A" → "restricted-byway2-a".</summary>
public sealed class RivetKebabCaseEnumConverter<T>()
    : RivetStringEnumConverter<T>(JsonNamingPolicy.KebabCaseLower)
    where T : struct, Enum;
