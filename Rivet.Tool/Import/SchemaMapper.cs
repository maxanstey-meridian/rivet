using System.Text.Json;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps JSON Schema objects from an OpenAPI spec to C# type representations.
/// Builds a registry of discovered records, enums, and branded value objects.
/// </summary>
internal static class SchemaMapper
{
    private static readonly HashSet<string> BrandFormats = ["email", "uri", "url", "uri-reference"];

    /// <summary>
    /// Walk #/components/schemas and return C# type representations.
    /// </summary>
    public static SchemaMapResult MapSchemas(JsonElement schemas, List<string> warnings)
    {
        var records = new List<GeneratedRecord>();
        var enums = new List<GeneratedEnum>();
        var brands = new List<GeneratedBrand>();

        foreach (var schema in schemas.EnumerateObject())
        {
            var name = schema.Name;
            var value = schema.Value;

            if (IsUnsupported(value, out var reason))
            {
                warnings.Add($"Schema '{name}': {reason} — skipped.");
                continue;
            }

            if (IsStringEnum(value))
            {
                enums.Add(MapEnum(name, value));
                continue;
            }

            if (IsBrandedString(value))
            {
                brands.Add(MapBrand(name, value));
                continue;
            }

            if (IsObject(value))
            {
                records.Add(MapRecord(name, value, warnings));
                continue;
            }

            // Primitive aliases (e.g. { "type": "string", "format": "date-time" }) — skip, resolved inline
        }

        return new SchemaMapResult(records, enums, brands);
    }

    /// <summary>
    /// Resolve a JSON Schema element to a C# type string.
    /// </summary>
    public static string ResolveCSharpType(JsonElement schema, List<string> warnings)
    {
        // $ref
        if (schema.TryGetProperty("$ref", out var refEl))
        {
            var refPath = refEl.GetString()!;
            var parts = refPath.Split('/');
            return parts[^1];
        }

        // Nullable: type is an array like ["string", "null"]
        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.Array)
        {
            var types = new List<string>();
            foreach (var t in typeEl.EnumerateArray())
            {
                types.Add(t.GetString()!);
            }

            var nonNull = types.FirstOrDefault(t => t != "null");
            if (nonNull is not null && types.Contains("null"))
            {
                var innerSchema = CloneWithType(schema, nonNull);
                var innerType = ResolveCSharpType(innerSchema, warnings);
                return innerType + "?";
            }
        }

        // type is a string
        if (schema.TryGetProperty("type", out var typeStr) && typeStr.ValueKind == JsonValueKind.String)
        {
            var type = typeStr.GetString()!;
            return type switch
            {
                "string" => ResolveStringType(schema),
                "integer" => ResolveIntegerType(schema),
                "number" => ResolveNumberType(schema),
                "boolean" => "bool",
                "array" => ResolveArrayType(schema, warnings),
                "object" => ResolveObjectType(schema, warnings),
                _ => WarnAndFallback(warnings, $"Unsupported JSON Schema type '{type}'"),
            };
        }

        // oneOf with null (nullable ref in 3.1)
        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.GetArrayLength() == 2)
        {
            JsonElement? refPart = null;
            var hasNull = false;

            foreach (var item in oneOf.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "null")
                {
                    hasNull = true;
                }
                else
                {
                    refPart = item;
                }
            }

            if (hasNull && refPart.HasValue)
            {
                return ResolveCSharpType(refPart.Value, warnings) + "?";
            }
        }

        return WarnAndFallback(warnings, "Schema could not be resolved to a C# type");
    }

    private static string WarnAndFallback(List<string> warnings, string reason)
    {
        warnings.Add($"{reason} — mapped to 'JsonElement'.");
        return "System.Text.Json.JsonElement";
    }

    private static string ResolveStringType(JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var fmt))
        {
            var format = fmt.GetString();
            return format switch
            {
                "date-time" => "DateTime",
                "guid" or "uuid" => "Guid",
                "binary" => "IFormFile",
                _ => "string",
            };
        }

        return "string";
    }

    private static string ResolveIntegerType(JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var fmt) && fmt.GetString() == "int64")
        {
            return "long";
        }

        return "int";
    }

    private static string ResolveNumberType(JsonElement schema)
    {
        if (schema.TryGetProperty("format", out var fmt) && fmt.GetString() == "float")
        {
            return "float";
        }

        return "double";
    }

    private static string ResolveArrayType(JsonElement schema, List<string> warnings)
    {
        if (schema.TryGetProperty("items", out var items))
        {
            var itemType = ResolveCSharpType(items, warnings);
            return $"List<{itemType}>";
        }

        return $"List<{WarnAndFallback(warnings, "Array schema missing 'items'")}>";
    }

    private static string ResolveObjectType(JsonElement schema, List<string> warnings)
    {
        if (schema.TryGetProperty("additionalProperties", out var addlProps))
        {
            var valueType = ResolveCSharpType(addlProps, warnings);
            return $"Dictionary<string, {valueType}>";
        }

        // Inline object with properties → unsupported
        if (schema.TryGetProperty("properties", out _))
        {
            return WarnAndFallback(warnings, "Inline anonymous object encountered");
        }

        return WarnAndFallback(warnings, "Unstructured object schema");
    }

    private static bool IsStringEnum(JsonElement schema)
    {
        return GetTypeString(schema) == "string"
            && schema.TryGetProperty("enum", out var enumEl)
            && enumEl.ValueKind == JsonValueKind.Array;
    }

    private static bool IsBrandedString(JsonElement schema)
    {
        if (GetTypeString(schema) != "string")
        {
            return false;
        }

        if (!schema.TryGetProperty("format", out var fmt))
        {
            return false;
        }

        var format = fmt.GetString();
        return format is not null && BrandFormats.Contains(format);
    }

    private static bool IsObject(JsonElement schema)
    {
        var type = GetTypeString(schema);
        return type == "object" || (type is null && schema.TryGetProperty("properties", out _));
    }

    private static bool IsUnsupported(JsonElement schema, out string reason)
    {
        if (schema.TryGetProperty("allOf", out _))
        {
            reason = "allOf composition is not supported";
            return true;
        }

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            // Allow nullable oneOf (exactly 2 items, one being null)
            if (oneOf.GetArrayLength() == 2)
            {
                var hasNull = false;
                foreach (var item in oneOf.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var t) && t.GetString() == "null")
                    {
                        hasNull = true;
                    }
                }

                if (hasNull)
                {
                    reason = "";
                    return false;
                }
            }

            reason = "oneOf composition is not supported";
            return true;
        }

        if (schema.TryGetProperty("anyOf", out _))
        {
            reason = "anyOf composition is not supported";
            return true;
        }

        if (schema.TryGetProperty("discriminator", out _))
        {
            reason = "discriminator is not supported";
            return true;
        }

        reason = "";
        return false;
    }

    private static GeneratedEnum MapEnum(string name, JsonElement schema)
    {
        var members = new List<string>();
        foreach (var member in schema.GetProperty("enum").EnumerateArray())
        {
            members.Add(Naming.ToPascalCaseFromSegments(member.GetString()!));
        }

        return new GeneratedEnum(name, members);
    }

    private static GeneratedBrand MapBrand(string name, JsonElement schema)
    {
        // All BrandFormats (email, uri, url, uri-reference) map to string.
        // Non-string brands (date-time, guid) are not in BrandFormats and
        // won't reach here — they're resolved inline by ResolveCSharpType.
        return new GeneratedBrand(name, "string");
    }

    private static GeneratedRecord MapRecord(string name, JsonElement schema, List<string> warnings)
    {
        var properties = new List<RecordProperty>();
        var requiredSet = new HashSet<string>();

        if (schema.TryGetProperty("required", out var reqArray))
        {
            foreach (var req in reqArray.EnumerateArray())
            {
                requiredSet.Add(req.GetString()!);
            }
        }

        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                var propName = Naming.ToPascalCaseFromSegments(prop.Name);
                var isRequired = requiredSet.Contains(prop.Name);
                var csharpType = ResolveCSharpType(prop.Value, warnings);

                // If not required and type isn't already nullable, make it nullable
                if (!isRequired && !csharpType.EndsWith("?"))
                {
                    csharpType += "?";
                }

                properties.Add(new RecordProperty(propName, csharpType, isRequired));
            }
        }

        return new GeneratedRecord(name, properties);
    }

    private static string? GetTypeString(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            return typeEl.GetString();
        }

        return null;
    }

    /// <summary>
    /// Creates a new JSON element that replaces the type array with a single type string.
    /// Used to resolve the non-null type from a nullable union.
    /// </summary>
    private static JsonElement CloneWithType(JsonElement original, string type)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            foreach (var prop in original.EnumerateObject())
            {
                if (prop.Name == "type")
                {
                    writer.WriteString("type", type);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(ms.ToArray());
    }

}

// --- Intermediate types ---

internal sealed record SchemaMapResult(
    IReadOnlyList<GeneratedRecord> Records,
    IReadOnlyList<GeneratedEnum> Enums,
    IReadOnlyList<GeneratedBrand> Brands);

internal sealed record GeneratedRecord(
    string Name,
    IReadOnlyList<RecordProperty> Properties);

internal sealed record RecordProperty(
    string Name,
    string CSharpType,
    bool IsRequired);

internal sealed record GeneratedEnum(
    string Name,
    IReadOnlyList<string> Members);

internal sealed record GeneratedBrand(
    string Name,
    string InnerType);
