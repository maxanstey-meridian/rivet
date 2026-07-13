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

    public static string EscapeLiteralSentinels(string json)
    {
        if (
            !json.Contains(NullSentinel, StringComparison.Ordinal)
            && !json.Contains(EscapePrefix, StringComparison.Ordinal)
        )
        {
            return json;
        }

        var root = JsonNode.Parse(json)!;
        var changed = false;
        EscapeExampleProperties(root, ref changed);
        return changed ? root.ToJsonString() : json;
    }

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

    private static void EscapeExampleProperties(JsonNode? node, ref bool changed)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                EscapeExampleProperties(item, ref changed);
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
                EscapePayload(value, ref changed);
            }
            else if (name == "examples")
            {
                EscapeExamples(value, ref changed);
            }
            else
            {
                EscapeExampleProperties(value, ref changed);
            }
        }
    }

    private static void EscapeExamples(JsonNode? node, ref bool changed)
    {
        if (node is JsonArray schemaExamples)
        {
            foreach (var example in schemaExamples.ToArray())
            {
                EscapePayload(example, ref changed);
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
                EscapePayload(value, ref changed);
            }
        }
    }

    private static void EscapePayload(JsonNode? node, ref bool changed)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text == NullSentinel || text.StartsWith(EscapePrefix, StringComparison.Ordinal))
                {
                    value.ReplaceWith(EscapePrefix + text);
                    changed = true;
                }
                break;
            case JsonArray array:
                foreach (var item in array.ToArray())
                {
                    EscapePayload(item, ref changed);
                }
                break;
            case JsonObject obj:
                foreach (var child in obj.Select(entry => entry.Value).ToArray())
                {
                    EscapePayload(child, ref changed);
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
