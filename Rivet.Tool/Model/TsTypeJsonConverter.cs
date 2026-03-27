using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

public sealed class TsTypeJsonConverter : JsonConverter<TsType>
{
    private static TsType DeserializeInner(JsonElement root, string propertyName, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<TsType>(root.GetProperty(propertyName).GetRawText(), options)
        ?? throw new JsonException($"Failed to deserialize '{propertyName}' as TsType.");

    public override TsType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kindElement))
            throw new JsonException("Missing 'kind' property on TsType.");
        var kind = kindElement.GetString()
            ?? throw new JsonException("'kind' property must be a string.");

        return kind switch
        {
            "primitive" => new TsType.Primitive(
                root.GetProperty("type").GetString()!,
                root.TryGetProperty("format", out var f) ? f.GetString() : null,
                root.TryGetProperty("csharpType", out var c) ? c.GetString() : null),

            "nullable" => new TsType.Nullable(
                DeserializeInner(root, "inner", options)),

            "array" => new TsType.Array(
                DeserializeInner(root, "element", options)),

            "dictionary" => new TsType.Dictionary(
                DeserializeInner(root, "value", options)),

            "stringUnion" => new TsType.StringUnion(
                root.GetProperty("values").EnumerateArray()
                    .Select(e => e.GetString()!).ToArray()),

            "intUnion" => new TsType.IntUnion(
                root.GetProperty("values").EnumerateArray()
                    .Select(e => e.GetInt32()).ToArray()),

            "ref" => new TsType.TypeRef(
                root.GetProperty("name").GetString()!),

            "generic" => new TsType.Generic(
                root.GetProperty("name").GetString()!,
                root.GetProperty("typeArgs").EnumerateArray()
                    .Select(e => JsonSerializer.Deserialize<TsType>(e.GetRawText(), options)!).ToArray()),

            "typeParam" => new TsType.TypeParam(
                root.GetProperty("name").GetString()!),

            "brand" => new TsType.Brand(
                root.GetProperty("name").GetString()!,
                DeserializeInner(root, "underlying", options)),

            "inlineObject" => new TsType.InlineObject(
                root.GetProperty("properties").EnumerateArray()
                    .Select(e => (
                        e.GetProperty("name").GetString()!,
                        JsonSerializer.Deserialize<TsType>(e.GetProperty("type").GetRawText(), options)!))
                    .ToArray()),

            _ => throw new JsonException($"Unknown TsType kind: '{kind}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, TsType value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case TsType.Primitive p:
                writer.WriteString("kind", "primitive");
                writer.WriteString("type", p.Name);
                if (p.Format is not null)
                    writer.WriteString("format", p.Format);
                if (p.CSharpType is not null)
                    writer.WriteString("csharpType", p.CSharpType);
                break;

            case TsType.Nullable n:
                writer.WriteString("kind", "nullable");
                writer.WritePropertyName("inner");
                JsonSerializer.Serialize(writer, n.Inner, options);
                break;

            case TsType.Array a:
                writer.WriteString("kind", "array");
                writer.WritePropertyName("element");
                JsonSerializer.Serialize(writer, a.Element, options);
                break;

            case TsType.Dictionary d:
                writer.WriteString("kind", "dictionary");
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, d.Value, options);
                break;

            case TsType.StringUnion su:
                writer.WriteString("kind", "stringUnion");
                writer.WriteStartArray("values");
                foreach (var member in su.Members)
                    writer.WriteStringValue(member);
                writer.WriteEndArray();
                break;

            case TsType.IntUnion iu:
                writer.WriteString("kind", "intUnion");
                writer.WriteStartArray("values");
                foreach (var member in iu.Members)
                    writer.WriteNumberValue(member);
                writer.WriteEndArray();
                break;

            case TsType.TypeRef tr:
                writer.WriteString("kind", "ref");
                writer.WriteString("name", tr.Name);
                break;

            case TsType.Generic g:
                writer.WriteString("kind", "generic");
                writer.WriteString("name", g.Name);
                writer.WriteStartArray("typeArgs");
                foreach (var arg in g.TypeArguments)
                    JsonSerializer.Serialize(writer, arg, options);
                writer.WriteEndArray();
                break;

            case TsType.TypeParam tp:
                writer.WriteString("kind", "typeParam");
                writer.WriteString("name", tp.Name);
                break;

            case TsType.Brand b:
                writer.WriteString("kind", "brand");
                writer.WriteString("name", b.Name);
                writer.WritePropertyName("underlying");
                JsonSerializer.Serialize(writer, b.Inner, options);
                break;

            case TsType.InlineObject obj:
                writer.WriteString("kind", "inlineObject");
                writer.WriteStartArray("properties");
                foreach (var (name, fieldType) in obj.Fields)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WritePropertyName("type");
                    JsonSerializer.Serialize(writer, fieldType, options);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;

            default:
                throw new JsonException($"Unsupported TsType subtype: {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }
}
