using System.Text.Json;
using Rivet.Tool.Model;

namespace Rivet.Tool.Emit;

/// <summary>
/// Shared logic for enriching a JSON/OpenAPI property schema dictionary
/// with metadata from a TsPropertyDefinition.
/// </summary>
internal static class SchemaEnricher
{
    public static void EnrichPropertySchema(Dictionary<string, object> propSchema, TsPropertyDefinition prop)
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
            try
            {
                propSchema["example"] = JsonSerializer.Deserialize<object>(prop.Example)!;
            }
            catch (JsonException)
            {
                propSchema["example"] = prop.Example;
            }
        }

        if (prop.IsReadOnly)
        {
            propSchema["readOnly"] = true;
        }

        if (prop.IsWriteOnly)
        {
            propSchema["writeOnly"] = true;
        }

        if (prop.Constraints is { } cc)
        {
            if (cc.MinLength.HasValue) propSchema["minLength"] = cc.MinLength.Value;
            if (cc.MaxLength.HasValue) propSchema["maxLength"] = cc.MaxLength.Value;
            if (cc.Pattern is not null) propSchema["pattern"] = cc.Pattern;
            if (cc.Minimum.HasValue) propSchema["minimum"] = cc.Minimum.Value;
            if (cc.Maximum.HasValue) propSchema["maximum"] = cc.Maximum.Value;
            if (cc.ExclusiveMinimum.HasValue) propSchema["exclusiveMinimum"] = cc.ExclusiveMinimum.Value;
            if (cc.ExclusiveMaximum.HasValue) propSchema["exclusiveMaximum"] = cc.ExclusiveMaximum.Value;
            if (cc.MultipleOf.HasValue) propSchema["multipleOf"] = cc.MultipleOf.Value;
            if (cc.MinItems.HasValue) propSchema["minItems"] = cc.MinItems.Value;
            if (cc.MaxItems.HasValue) propSchema["maxItems"] = cc.MaxItems.Value;
            if (cc.UniqueItems == true) propSchema["uniqueItems"] = true;
        }
    }
}
