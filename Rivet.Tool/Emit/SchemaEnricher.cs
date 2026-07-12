using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Shared logic for enriching a JSON/OpenAPI property schema dictionary
/// with metadata from a TsPropertyDefinition.
/// </summary>
internal static class SchemaEnricher
{
    public static void EnrichPropertySchema(
        Dictionary<string, object> propSchema,
        TsPropertyDefinition prop
    )
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
            try
            {
                propSchema["default"] = JsonSerializer.Deserialize<object>(prop.DefaultValue)!;
            }
            catch (JsonException)
            {
                // Invalid JSON literal — emit as raw string rather than crashing
                propSchema["default"] = prop.DefaultValue;
            }
        }

        if (prop.Example is not null)
        {
            // OpenAPI 3.1 / JSON Schema 2020-12: schema-level `example` is replaced by
            // the `examples` keyword (an array of example values).
            object exampleValue;
            try
            {
                exampleValue = JsonSerializer.Deserialize<object>(prop.Example)!;
            }
            catch (JsonException)
            {
                // Invalid JSON literal — emit as raw string rather than crashing
                exampleValue = prop.Example;
            }

            propSchema["examples"] = new List<object> { exampleValue };
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

    public static void EnrichConstraints(
        Dictionary<string, object> schema,
        TsPropertyConstraints? constraints
    )
    {
        if (constraints is { } cc)
        {
            if (cc.MinLength.HasValue)
            {
                schema["minLength"] = cc.MinLength.Value;
            }

            if (cc.MaxLength.HasValue)
            {
                schema["maxLength"] = cc.MaxLength.Value;
            }

            if (cc.Pattern is not null)
            {
                schema["pattern"] = cc.Pattern;
            }

            if (cc.Minimum.HasValue)
            {
                schema["minimum"] = cc.Minimum.Value;
            }

            if (cc.Maximum.HasValue)
            {
                schema["maximum"] = cc.Maximum.Value;
            }

            if (cc.ExclusiveMinimum.HasValue)
            {
                schema["exclusiveMinimum"] = cc.ExclusiveMinimum.Value;
            }

            if (cc.ExclusiveMaximum.HasValue)
            {
                schema["exclusiveMaximum"] = cc.ExclusiveMaximum.Value;
            }

            if (cc.MultipleOf.HasValue)
            {
                schema["multipleOf"] = cc.MultipleOf.Value;
            }

            if (cc.MinItems.HasValue)
            {
                schema["minItems"] = cc.MinItems.Value;
            }

            if (cc.MaxItems.HasValue)
            {
                schema["maxItems"] = cc.MaxItems.Value;
            }

            if (cc.UniqueItems == true)
            {
                schema["uniqueItems"] = true;
            }
        }
    }
}
