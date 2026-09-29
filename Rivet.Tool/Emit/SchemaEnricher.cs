using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Shared logic for enriching a JSON/OpenAPI property schema with metadata from a
/// TsPropertyDefinition.
/// </summary>
internal static class SchemaEnricher
{
    private static readonly JsonSerializerOptions _constraintOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void EnrichPropertySchema(JsonObject propSchema, TsPropertyDefinition prop)
    {
        if (prop.Description is not null)
        {
            propSchema["description"] = prop.Description;
        }

        if (prop.IsDeprecated)
        {
            propSchema["deprecated"] = true;
        }

        if (prop.DefaultValue is not null)
        {
            propSchema["default"] = ParseJsonLiteral(
                prop.DefaultValue,
                $"default of property '{prop.Name}'"
            );
        }

        if (prop.Example is not null)
        {
            // OpenAPI 3.1 / JSON Schema 2020-12: schema-level `example` is replaced by
            // the `examples` keyword (an array of example values).
            propSchema["examples"] = new JsonArray(
                ParseJsonLiteral(prop.Example, $"example of property '{prop.Name}'")
            );
        }

        if (prop.IsReadOnly)
        {
            propSchema["readOnly"] = true;
        }

        if (prop.IsWriteOnly)
        {
            propSchema["writeOnly"] = true;
        }

        EnrichConstraints(propSchema, prop.Constraints);

        if (prop.Format is not null)
        {
            propSchema["format"] = prop.Format;
        }
    }

    public static void EnrichConstraints(JsonObject schema, TsPropertyConstraints? constraints)
    {
        if (constraints is null)
        {
            return;
        }

        var keywords = JsonSerializer.SerializeToNode(constraints, _constraintOptions)!.AsObject();
        foreach (var (keyword, value) in keywords.ToList())
        {
            keywords.Remove(keyword);
            // uniqueItems: false is the JSON Schema default, so only true is a constraint.
            if (keyword != "uniqueItems" || value!.GetValue<bool>())
            {
                schema[keyword] = value;
            }
        }
    }

    /// <summary>
    /// Parses an authored JSON literal (<c>[RivetDefault]</c>, <c>[RivetExample]</c>, scalar
    /// metadata); anything else is a user error rather than a guess.
    /// </summary>
    public static JsonNode? ParseJsonLiteral(string json, string what)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new RivetUserException(
                $"error: {what} must be a JSON literal, got '{json}' ({exception.Message})"
            );
        }
    }
}
