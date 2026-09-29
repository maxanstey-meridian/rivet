using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

/// <summary>
/// Integer enum members travel as decimal strings in the IR, so every legal C# enum constant
/// (long/ulong, negative) survives without an Int32 assumption; on the wire they are exact
/// JSON integers.
/// </summary>
public static class IntEnumLiteral
{
    /// <summary>The exact Int64 or UInt64 JSON number for a decimal member literal.</summary>
    public static JsonValue ToJson(string literal)
    {
        if (
            long.TryParse(
                literal,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var signed
            )
        )
        {
            return JsonValue.Create(signed);
        }

        if (
            ulong.TryParse(
                literal,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unsigned
            )
        )
        {
            return JsonValue.Create(unsigned);
        }

        throw new JsonException(
            $"Integer enum value '{literal}' is not a decimal integer in the Int64/UInt64 range."
        );
    }

    /// <summary>Reads one JSON integer member as its decimal literal.</summary>
    public static string Read(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("Integer enum values must be JSON integers, not strings.");
        }

        if (reader.TryGetInt64(out var signed))
        {
            return signed.ToString(CultureInfo.InvariantCulture);
        }

        if (reader.TryGetUInt64(out var unsigned))
        {
            return unsigned.ToString(CultureInfo.InvariantCulture);
        }

        throw new JsonException(
            "Integer enum values must be decimal integers in the Int64/UInt64 range."
        );
    }
}

public sealed class IntEnumValuesJsonConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Integer enum values must be a JSON array.");
        }

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(IntEnumLiteral.Read(ref reader));
        }

        return values;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<string> value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartArray();
        foreach (var member in value)
        {
            IntEnumLiteral.ToJson(member).WriteTo(writer);
        }
        writer.WriteEndArray();
    }
}
