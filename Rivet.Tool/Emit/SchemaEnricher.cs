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
        Annotate(
            propSchema,
            prop.Description,
            prop.IsDeprecated,
            prop.DefaultValue,
            prop.Example,
            $"property '{prop.Name}'"
        );

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

    /// <summary>
    /// The annotations a property's schema carries beside its type, whether the property is a
    /// record's or a form field's: a description, deprecated, a default, and an example, each
    /// an authored JSON literal. JSON Schema 2020-12 has no singular <c>example</c>, so it
    /// joins the schema's <c>examples</c>.
    /// </summary>
    public static void Annotate(
        JsonObject schema,
        string? description,
        bool isDeprecated,
        string? defaultJson,
        string? exampleJson,
        string what
    )
    {
        if (description is not null)
        {
            schema["description"] = description;
        }

        if (isDeprecated)
        {
            schema["deprecated"] = true;
        }

        if (defaultJson is not null)
        {
            schema["default"] = ParseJsonLiteral(defaultJson, $"default of {what}");
        }

        if (exampleJson is not null)
        {
            var example = ParseJsonLiteral(exampleJson, $"example of {what}");
            if (schema["examples"] is JsonArray examples)
            {
                examples.Add(example);
            }
            else
            {
                schema["examples"] = new JsonArray(example);
            }
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
            throw Diagnostics.Error(
                Diagnostics.InvalidBuilderArgument,
                $"{what} must be a JSON literal, got '{json}' ({exception.Message})"
            );
        }
    }

    /// <summary>
    /// <see cref="ParseJsonLiteral"/> for a keyword JSON Schema defines as an array
    /// (<c>enum</c>, <c>examples</c>): any other literal is a user error, not a schema.
    /// </summary>
    public static JsonArray ParseJsonArray(string json, string what) =>
        ParseJsonLiteral(json, what) as JsonArray
        ?? throw Diagnostics.Error(
            Diagnostics.InvalidBuilderArgument,
            $"{what} must be a JSON array, got '{json}'"
        );

    /// <summary>
    /// A header's or parameter's <c>examples</c>: a map of Example objects by name, unlike a
    /// schema's <c>examples</c> array. Anything else would be written into the spec as-is.
    /// </summary>
    public static JsonObject ExampleMap(JsonElement examples, string what)
    {
        if (
            examples.ValueKind != JsonValueKind.Object
            || examples
                .EnumerateObject()
                .Any(entry => entry.Value.ValueKind != JsonValueKind.Object)
        )
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidBuilderArgument,
                $"{what} must be a JSON object mapping names to Example objects, got '{examples.GetRawText()}'"
            );
        }
        return JsonNode.Parse(examples.GetRawText())!.AsObject();
    }
}
