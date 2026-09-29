using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Rivet.Tool.Import;

internal static class OpenApiJsonNodeSerializer
{
    private const string NullSentinel =
        "openapi-json-null-sentinel-value-2BF93600-0FE4-4250-987A-E5DDB203E464";
    private const string EscapePrefix =
        "rivet-openapi-json-null-sentinel-literal-7A8FD841-72EC-49C9-8C69-7AFA214B51A3-";

    /// <summary>
    /// Microsoft.OpenApi reads a JSON null inside an example as its null sentinel string, so
    /// escape any authored string that collides with the sentinel or with the escape prefix.
    /// </summary>
    public static void EscapeLiteralSentinels(JsonNode root) => EscapeExampleProperties(root);

    public static string Serialize(JsonNode node)
    {
        if (JsonNullSentinel.IsJsonNullSentinel(node))
        {
            return "null";
        }
        if (
            node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.StartsWith(EscapePrefix, StringComparison.Ordinal)
        )
        {
            return JsonSerializer.Serialize(text[EscapePrefix.Length..]);
        }

        var clone = node.DeepClone();
        RestoreEscapedStrings(clone);
        return clone.ToJsonString();
    }

    public static JsonNode? Clone(JsonNode node) => JsonNode.Parse(Serialize(node));

    private static void EscapeExampleProperties(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                EscapeExampleProperties(item);
            }
            return;
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var (name, value) in obj.ToArray())
        {
            if (name == "example")
            {
                EscapePayload(value);
            }
            else if (name == "examples")
            {
                EscapeExamples(value);
            }
            else
            {
                EscapeExampleProperties(value);
            }
        }
    }

    private static void EscapeExamples(JsonNode? node)
    {
        if (node is JsonArray schemaExamples)
        {
            foreach (var example in schemaExamples.ToArray())
            {
                EscapePayload(example);
            }
            return;
        }

        if (node is not JsonObject namedExamples)
        {
            return;
        }

        foreach (var example in namedExamples.Select(entry => entry.Value).OfType<JsonObject>())
        {
            if (example.TryGetPropertyValue("value", out var value))
            {
                EscapePayload(value);
            }
        }
    }

    private static void EscapePayload(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text == NullSentinel || text.StartsWith(EscapePrefix, StringComparison.Ordinal))
                {
                    value.ReplaceWith(EscapePrefix + text);
                }
                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                {
                    EscapePayload(item);
                }
                break;
            case JsonObject obj:
                foreach (var child in obj.Select(entry => entry.Value).ToArray())
                {
                    EscapePayload(child);
                }
                break;
        }
    }

    private static void RestoreEscapedStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value
                when value.TryGetValue<string>(out var text)
                    && text.StartsWith(EscapePrefix, StringComparison.Ordinal):
                value.ReplaceWith(text[EscapePrefix.Length..]);
                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                {
                    RestoreEscapedStrings(item);
                }
                break;
            case JsonObject obj:
                foreach (var child in obj.Select(entry => entry.Value).ToArray())
                {
                    RestoreEscapedStrings(child);
                }
                break;
        }
    }
}
