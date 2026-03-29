using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.OpenApi;

namespace Rivet.Tool.Import;

/// <summary>
/// Pure static helpers for classifying OpenAPI schemas, resolving primitive types,
/// reading vendor extensions, and structural fingerprinting.
/// </summary>
internal static class SchemaClassifier
{
    private static readonly HashSet<string> BrandFormats = ["email", "uri", "url", "uri-reference"];

    // --- Predicates ---

    internal static bool IsStringEnum(IOpenApiSchema schema)
    {
        if (schema.Enum is not { Count: > 0 })
            return false;

        // Explicit type: string
        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.String))
            return true;

        // No type declared — infer from values (common in real-world specs)
        if (!schema.Type.HasValue)
            return schema.Enum.All(v => v is null || v is JsonNode node && node.GetValueKind() == JsonValueKind.String);

        return false;
    }

    internal static bool IsIntEnum(IOpenApiSchema schema)
    {
        if (schema.Enum is not { Count: > 1 })
            return false;

        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.Integer))
            return true;

        // No explicit type — infer from values
        if (!schema.Type.HasValue)
            return schema.Enum.All(v => v is JsonNode node && node.GetValueKind() == JsonValueKind.Number);

        return false;
    }

    internal static bool IsBrand(IOpenApiSchema schema)
    {
        // x-rivet-brand extension is authoritative — works for any underlying type
        if (HasExtension(schema, "x-rivet-brand"))
        {
            return true;
        }

        // Heuristic: string schemas with known branded formats (email, uri, etc.)
        if (!schema.Type.HasValue || !schema.Type.Value.HasFlag(JsonSchemaType.String))
        {
            return false;
        }

        return schema.Format is not null && BrandFormats.Contains(schema.Format);
    }

    internal static bool IsObject(IOpenApiSchema schema)
    {
        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.Object))
        {
            return true;
        }

        return !schema.Type.HasValue && schema.Properties is { Count: > 0 };
    }

    internal static bool IsNullableOneOf(IList<IOpenApiSchema> oneOfList)
    {
        if (oneOfList.Count != 2)
        {
            return false;
        }

        foreach (var item in oneOfList)
        {
            if (item.Type.HasValue && item.Type.Value == JsonSchemaType.Null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if MapSchemas would generate a record, enum, or brand for this schema.
    /// </summary>
    internal static bool WouldGenerateType(IOpenApiSchema schema)
    {
        if (IsStringEnum(schema))
        {
            return true;
        }

        if (IsIntEnum(schema))
        {
            return true;
        }

        if (IsBrand(schema))
        {
            return true;
        }

        if (schema.AllOf is { Count: > 0 })
        {
            return true;
        }

        if (schema.OneOf is { Count: > 0 } && !IsNullableOneOf(schema.OneOf))
        {
            return true;
        }

        if (schema.AnyOf is { Count: > 1 })
        {
            return true;
        }

        if (IsObject(schema) && schema.Properties is { Count: > 0 })
        {
            return true;
        }

        if (HasExtension(schema, "x-rivet-empty-record"))
        {
            return true;
        }

        return false;
    }

    internal static bool HasResolvableProperties(IOpenApiSchema schema)
    {
        return schema.AllOf is { Count: > 0 }
            || schema.OneOf is { Count: > 0 }
            || schema.AnyOf is { Count: > 0 }
            || schema.Properties is { Count: > 0 }
            || schema.Items is not null
            || schema.AdditionalProperties is not null
            || schema.Enum is { Count: > 0 }
            || schema.Const is not null;
    }

    // --- Primitive type resolvers ---

    internal static string? ResolvePrimitiveType(IOpenApiSchema schema)
    {
        if (!schema.Type.HasValue)
        {
            return null;
        }

        var type = schema.Type.Value & ~JsonSchemaType.Null;
        return type switch
        {
            JsonSchemaType.String => "string",
            JsonSchemaType.Integer => ResolveIntegerType(schema),
            JsonSchemaType.Number => ResolveNumberType(schema),
            JsonSchemaType.Boolean => "bool",
            _ => null,
        };
    }

    internal static string ResolveStringType(IOpenApiSchema schema)
    {
        if (schema.Format is "binary" || HasExtension(schema, "x-rivet-file"))
        {
            return "IFormFile";
        }

        return schema.Format switch
        {
            "date-time" => "DateTime",
            "date" => "DateOnly",
            "time" => "TimeOnly",
            "guid" or "uuid" => "Guid",
            "uri" => "Uri",
            _ => "string",
        };
    }

    internal static string ResolveIntegerType(IOpenApiSchema schema)
    {
        return schema.Format switch
        {
            "int32" => "int",
            "int64" => "long",
            "int16" => "short",
            "uint16" => "ushort",
            "int8" => "sbyte",
            "uint8" => "byte",
            "uint32" => "uint",
            "uint64" => "ulong",
            _ => "long", // bare integer (no format) → long to avoid narrowing
        };
    }

    internal static string ResolveNumberType(IOpenApiSchema schema)
    {
        return schema.Format switch
        {
            "float" => "float",
            "decimal" => "decimal",
            _ => "double",
        };
    }

    internal static string ResolveJsonNodeFqn(string shortName)
    {
        return shortName switch
        {
            "JsonObject" => "System.Text.Json.Nodes.JsonObject",
            "JsonArray" => "System.Text.Json.Nodes.JsonArray",
            "JsonNode" => "System.Text.Json.Nodes.JsonNode",
            _ => shortName,
        };
    }

    internal static string PrimitiveDisplayName(string csharpType)
    {
        return csharpType switch
        {
            "string" => "String",
            "int" => "Int",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "bool" => "Bool",
            "DateTime" => "DateTime",
            "Guid" => "Guid",
            "System.Text.Json.JsonElement" => "Object",
            _ when csharpType.StartsWith("List<") || csharpType.StartsWith("IReadOnlyList<") =>
                "ListOf" + Naming.StripInvalidIdentifierChars(csharpType),
            _ when csharpType.StartsWith("Dictionary<string,") =>
                "DictionaryOf" + Naming.StripInvalidIdentifierChars(csharpType),
            _ => Naming.StripInvalidIdentifierChars(Naming.ToPascalCaseFromSegments(csharpType)),
        };
    }

    // --- Extension readers ---

    internal static bool HasExtension(IOpenApiSchema schema, string key)
    {
        return schema.Extensions is not null && schema.Extensions.ContainsKey(key);
    }

    internal static string? GetExtensionString(IOpenApiSchema schema, string key)
    {
        if (schema.Extensions is null || !schema.Extensions.TryGetValue(key, out var ext))
        {
            return null;
        }

        if (ext is JsonNodeExtension jsonExt)
        {
            return jsonExt.Node?.GetValue<string>();
        }

        return null;
    }

    internal static bool TryGetGenericExtension(IOpenApiSchema schema, out GenericTemplateInfo? info)
    {
        info = null;
        if (schema.Extensions is null || !schema.Extensions.TryGetValue("x-rivet-generic", out var ext))
        {
            return false;
        }

        if (ext is not JsonNodeExtension jsonExt || jsonExt.Node is not JsonObject obj)
        {
            return false;
        }

        var name = obj["name"]?.GetValue<string>();
        if (name is null)
        {
            return false;
        }

        var typeParams = new List<string>();
        if (obj["typeParams"] is JsonArray paramsArr)
        {
            foreach (var p in paramsArr)
            {
                var val = p?.GetValue<string>();
                if (val is not null)
                {
                    typeParams.Add(val);
                }
            }
        }

        var args = new Dictionary<string, string>();
        if (obj["args"] is JsonObject argsObj)
        {
            foreach (var (k, v) in argsObj)
            {
                var val = v?.GetValue<string>();
                if (val is not null)
                {
                    args[k] = val;
                }
            }
        }

        info = new GenericTemplateInfo(name, typeParams, args);
        return true;
    }

    // --- Naming / dedup ---

    internal static List<RecordProperty> DeduplicateProperties(List<RecordProperty> properties)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<RecordProperty>(properties.Count);

        foreach (var prop in properties)
        {
            var name = prop.Name;
            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                result.Add(prop with { Name = $"{name}_{count}" });
            }
            else
            {
                seen[name] = 1;
                result.Add(prop);
            }
        }

        return result;
    }

    // --- Enum / Brand builders ---

    internal static GeneratedEnum MapEnum(string name, IOpenApiSchema schema)
    {
        var seen = new Dictionary<string, int>();
        var members = new List<GeneratedEnumMember>();
        foreach (var member in schema.Enum!)
        {
            if (member is null)
            {
                continue;
            }

            var original = member.ToString();
            var sanitized = Naming.ToPascalCaseFromSegments(original);
            if (seen.TryGetValue(sanitized, out var count))
            {
                count++;
                seen[sanitized] = count;
                var deduped = $"{sanitized}_{count}";
                members.Add(new GeneratedEnumMember(deduped, original));
            }
            else
            {
                seen[sanitized] = 1;
                // Only store original name if it differs from the C# name
                var originalName = string.Equals(sanitized, original, StringComparison.Ordinal) ? null : original;
                members.Add(new GeneratedEnumMember(sanitized, originalName));
            }
        }

        return new GeneratedEnum(name, members);
    }

    internal static GeneratedEnum MapIntEnum(string name, IOpenApiSchema schema)
    {
        var seen = new Dictionary<string, int>();
        var members = new List<GeneratedEnumMember>();
        foreach (var member in schema.Enum!)
        {
            if (member is null)
                continue;

            var intVal = member.GetValue<int>();
            var csharpName = intVal < 0 ? $"ValueNeg{Math.Abs(intVal)}" : $"Value{intVal}";

            if (seen.TryGetValue(csharpName, out var count))
            {
                count++;
                seen[csharpName] = count;
                csharpName = $"{csharpName}_{count}";
            }
            else
            {
                seen[csharpName] = 1;
            }

            members.Add(new GeneratedEnumMember(csharpName, null, intVal));
        }

        return new GeneratedEnum(name, members);
    }

    internal static GeneratedBrand MapBrand(string name, IOpenApiSchema schema)
    {
        var brandName = GetExtensionString(schema, "x-rivet-brand") ?? name;
        var innerType = ResolvePrimitiveType(schema) ?? "string";
        return new GeneratedBrand(brandName, innerType);
    }

    // --- Generic type helpers ---

    internal static string BuildGenericTypeString(GenericTemplateInfo info)
    {
        // Build e.g. "PagedResult<TaskDto>" from template name and args
        var argStrings = info.TypeParams.Select(tp =>
            info.Args.TryGetValue(tp, out var concrete) ? concrete : tp);
        return $"{info.Name}<{string.Join(", ", argStrings)}>";
    }

    internal static string ReverseSubstituteTypes(string csharpType, Dictionary<string, string> reverseMap)
    {
        // Replace concrete type names with type parameter names using word-boundary
        // matching to avoid corrupting types that contain the concrete name as a substring
        // (e.g. replacing "Task" must not corrupt "TaskStatus" into "TStatus").
        var result = csharpType;
        foreach (var (concreteType, typeParam) in reverseMap.OrderByDescending(kv => kv.Key.Length))
        {
            result = Regex.Replace(result, @"\b" + Regex.Escape(concreteType) + @"\b", typeParam);
        }

        return result;
    }

    // --- Structural fingerprinting ---

    /// <summary>
    /// Computes a structural fingerprint for an inline schema.
    /// </summary>
    internal static string ComputeSchemaFingerprint(IOpenApiSchema schema)
    {
        var sb = new StringBuilder();
        AppendSchemaFingerprint(sb, schema, 0);
        return sb.ToString();
    }

    private static void AppendSchemaFingerprint(StringBuilder sb, IOpenApiSchema schema, int depth)
    {
        if (depth > 10)
        {
            sb.Append("...");
            return;
        }

        sb.Append('{');
        sb.Append("t:").Append(schema.Type.HasValue ? (int)schema.Type.Value : -1);

        if (schema.Format is not null)
        {
            sb.Append(",f:").Append(schema.Format);
        }

        if (schema.Properties is { Count: > 0 })
        {
            sb.Append(",p:{");
            foreach (var (k, v) in schema.Properties.OrderBy(p => p.Key))
            {
                sb.Append(k).Append(':');
                if (v is OpenApiSchemaReference propRef)
                {
                    sb.Append("$ref:").Append(propRef.Reference.Id);
                }
                else
                {
                    AppendSchemaFingerprint(sb, v, depth + 1);
                }

                sb.Append(',');
            }

            sb.Append('}');
        }

        if (schema.Required is { Count: > 0 })
        {
            sb.Append(",r:").Append(string.Join(",", schema.Required.OrderBy(r => r)));
        }

        if (schema.Items is not null)
        {
            sb.Append(",i:");
            if (schema.Items is OpenApiSchemaReference itemRef)
            {
                sb.Append("$ref:").Append(itemRef.Reference.Id);
            }
            else
            {
                AppendSchemaFingerprint(sb, schema.Items, depth + 1);
            }
        }

        if (schema.Enum is { Count: > 0 })
        {
            sb.Append(",e:").Append(string.Join(",", schema.Enum.Select(e => e?.ToString())));
        }

        if (schema.AdditionalProperties is not null)
        {
            sb.Append(",ap:");
            if (schema.AdditionalProperties is OpenApiSchemaReference apRef)
            {
                sb.Append("$ref:").Append(apRef.Reference.Id);
            }
            else
            {
                AppendSchemaFingerprint(sb, schema.AdditionalProperties, depth + 1);
            }
        }

        sb.Append('}');
    }
}
