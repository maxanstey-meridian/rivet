using System.Globalization;
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
    private static readonly HashSet<string> _brandFormats =
    [
        "email",
        "uri",
        "url",
        "uri-reference",
    ];

    // --- Predicates ---

    internal static bool IsStringEnum(IOpenApiSchema schema)
    {
        if (schema.Enum is not { Count: > 0 })
        {
            return false;
        }

        // Explicit type: string
        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.String))
        {
            return true;
        }

        // No type declared — infer from values (common in real-world specs)
        if (!schema.Type.HasValue)
        {
            return schema.Enum.All(v =>
                v is null || v is JsonNode node && node.GetValueKind() == JsonValueKind.String
            );
        }

        return false;
    }

    internal static bool IsIntEnum(IOpenApiSchema schema)
    {
        if (schema.Enum is not { Count: > 0 })
        {
            return false;
        }

        // The member set must be expressible as one legal C# enum: a negative
        // constant cannot coexist with a constant above long.MaxValue in any
        // underlying type, so such a schema is not classified as an integer enum —
        // it degrades through the existing dropped-constraint warning instead of
        // generating uncompilable C#. Only Number-kind nodes participate; other
        // enum entries are rejected by the IsWholeInt64 gate below anyway.
        var numberNodes = schema
            .Enum.OfType<JsonNode>()
            .Where(node => node.GetValueKind() == JsonValueKind.Number)
            .ToList();
        var hasNegative = numberNodes.Any(node =>
            node.AsValue().TryGetValue<long>(out var n) && n < 0
        );
        var hasBeyondInt64 = numberNodes.Any(node => !node.AsValue().TryGetValue<long>(out _));
        if (hasNegative && hasBeyondInt64)
        {
            return false;
        }

        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.Integer))
        {
            return schema.Enum.All(v => v is JsonNode node && IsWholeInt64(node));
        }

        // No explicit type — infer from values
        if (!schema.Type.HasValue)
        {
            return schema.Enum.All(v => v is JsonNode node && IsWholeInt64(node));
        }

        return false;
    }

    /// <summary>
    /// True for any whole JSON number inside the signed/unsigned 64-bit ranges —
    /// the widest exact carrier the generated C# enum emitter guarantees, so legal
    /// enum constants beyond Int32 survive import instead of being dropped
    /// (planner-constraint:generated-enum-underlying-type).
    /// </summary>
    internal static bool IsWholeInt64(JsonNode node)
    {
        if (node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (node.AsValue().TryGetValue<long>(out _))
        {
            return true;
        }

        // TryGetValue<long> fails for values above long.MaxValue (e.g. 2^63) —
        // parse the raw digits as ulong so unsigned Int64-range constants survive.
        return ulong.TryParse(
            node.ToJsonString().AsSpan().Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out _
        );
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

        return schema.Format is not null && _brandFormats.Contains(schema.Format);
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
            if (IsNullOnlyBranch(item))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsNullOnlyBranch(IOpenApiSchema schema)
    {
        if (schema.Type is not { } type || !type.HasFlag(JsonSchemaType.Null))
        {
            return false;
        }

        var nonNullType = type & ~JsonSchemaType.Null;
        return nonNullType == 0 || nonNullType == JsonSchemaType.String && schema.Pattern == ".^";
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
            // Must agree with MapSchemas (I2): an allOf whose merged record would have zero
            // properties is SKIPPED there, so refs to it must not resolve to the (never
            // emitted) record name. Mirrors ResolveAllOfRecord + MergeWithSiblingProperties.
            return AllOfYieldsProperties(schema);
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

    /// <summary>
    /// Static mirror of RecordSynthesizer.ResolveAllOfRecord + MergeWithSiblingProperties:
    /// would this allOf schema's merged record contain at least one property?
    /// Sibling properties count at the top level; allOf elements contribute their own
    /// properties, or (for elements that themselves compose allOf) their allOf elements'.
    /// </summary>
    private static bool AllOfYieldsProperties(IOpenApiSchema schema, int depth = 0)
    {
        if (depth > 25)
        {
            return true; // pathological nesting — assume a record is generated
        }

        if (schema.Properties is { Count: > 0 })
        {
            return true;
        }

        if (schema.AllOf is not { Count: > 0 })
        {
            return false;
        }

        return schema.AllOf.Any(element => AllOfElementContributes(element, depth));
    }

    private static bool AllOfElementContributes(IOpenApiSchema element, int depth)
    {
        if (depth > 25)
        {
            return true; // pathological nesting — assume a record is generated
        }

        // ResolveAllOfRecord recurses into a REF element's allOf when present AND merges the
        // target's own sibling properties (I4 fix); all other elements contribute their own
        // properties only.
        if (element is OpenApiSchemaReference && element.AllOf is { Count: > 0 })
        {
            return element.Properties is { Count: > 0 }
                || element.AllOf.Any(nested => AllOfElementContributes(nested, depth + 1));
        }

        return element.Properties is { Count: > 0 };
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
            "JsonElement" => "System.Text.Json.JsonElement",
            "JsonDocument" => "System.Text.Json.JsonDocument",
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
            _ when csharpType.StartsWith("global::System.", StringComparison.Ordinal) => csharpType[
                "global::System.".Length..
            ],
            _ when csharpType.StartsWith("List<") || csharpType.StartsWith("IReadOnlyList<") =>
                "ListOf" + Naming.StripInvalidIdentifierChars(csharpType),
            _ when csharpType.StartsWith("Dictionary<string,") => "DictionaryOf"
                + Naming.StripInvalidIdentifierChars(csharpType),
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

    internal static List<string>? GetExtensionStringArray(IOpenApiSchema schema, string key)
    {
        if (schema.Extensions is null || !schema.Extensions.TryGetValue(key, out var ext))
        {
            return null;
        }

        if (ext is not JsonNodeExtension jsonExt || jsonExt.Node is not JsonArray arr)
        {
            return null;
        }

        var result = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var val = item?.GetValue<string>();
            if (val is null)
            {
                return null; // any non-string element → bail
            }

            result.Add(val);
        }

        return result;
    }

    internal static bool TryGetGenericExtension(
        IOpenApiSchema schema,
        out GenericTemplateInfo? info
    )
    {
        info = null;
        if (
            schema.Extensions is null
            || !schema.Extensions.TryGetValue("x-rivet-generic", out var ext)
        )
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
        var used = new HashSet<string>(StringComparer.Ordinal);
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<RecordProperty>(properties.Count);

        foreach (var prop in properties)
        {
            var name = prop.Name;
            if (!used.Add(name))
            {
                var suffix = nextSuffix.GetValueOrDefault(name, 2);
                do
                {
                    name = $"{prop.Name}_{suffix++}";
                } while (!used.Add(name));

                nextSuffix[prop.Name] = suffix;
            }

            result.Add(prop with { Name = name });
        }

        return result;
    }

    // --- Enum / Brand builders ---

    internal static GeneratedEnum MapEnum(string name, IOpenApiSchema schema)
    {
        // A Rivet-emitted spec declares the wire casing via
        // x-rivet-enum-naming-policy — the imported enum keeps the family
        // converter and needs member pins only where the wire value differs
        // from the policy-cased name. An unknown token is ignored (not guessed):
        // the schema then imports through the exact-pin path like any foreign
        // spec.
        var policyToken = GetExtensionString(schema, "x-rivet-enum-naming-policy");
        var policy =
            policyToken is not null && Naming.TryPolicyFromToken(policyToken, out var parsed)
                ? parsed
                : (RivetNamingPolicy?)null;

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
                // Pin when the EMITTED wire value would differ from the original.
                // The emitted casing follows the enum's declared converter: the
                // policy-cased member name when a policy is declared, otherwise
                // the emitter's camelCase (TypeWalker). 'Ready' (Pascal ==
                // original, old check skipped the pin) still emits as 'ready' —
                // a silent case-mangle both directions
                // (FABLE_ROUNDTRIP #3, 63 properties on the github corpus).
                var derived = policy is null
                    ? Naming.ToCamelCase(sanitized)
                    : Naming.ToPolicyCase(sanitized, policy.Value);
                var originalName = string.Equals(derived, original, StringComparison.Ordinal)
                    ? null
                    : original;
                members.Add(new GeneratedEnumMember(sanitized, originalName));
            }
        }

        return new GeneratedEnum(
            name,
            members,
            schema.Format,
            schema.Description,
            NamingPolicy: policy is null ? null : Naming.ToPolicyToken(policy.Value)
        );
    }

    internal static GeneratedEnum MapIntEnum(string name, IOpenApiSchema schema)
    {
        var varnames = GetExtensionStringArray(schema, "x-enum-varnames");
        var useVarnames = varnames is not null && varnames.Count == schema.Enum!.Count;

        var emitted = new HashSet<string>();
        var members = new List<GeneratedEnumMember>();
        var index = 0;
        foreach (var member in schema.Enum!)
        {
            if (member is not JsonNode memberNode || !IsWholeInt64(memberNode))
            {
                index++;
                continue;
            }

            // Naming magnitude only — the member value below keeps raw digits.
            // GetValue<long> throws above long.MaxValue even though IsWholeInt64
            // admits those digits, and Math.Abs overflows at long.MinValue, so
            // derive the name from a defensive magnitude read instead
            // (acceptance:numeric-enums-cover-all-legal-underlying-values).
            string namingMagnitude;
            if (memberNode.AsValue().TryGetValue<long>(out var signedValue))
            {
                namingMagnitude = signedValue.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                // IsWholeInt64 vetted the raw digits as a legal ulong constant.
                namingMagnitude = memberNode.ToJsonString().Trim();
            }

            var csharpName =
                useVarnames ? Naming.ToPascalCaseFromSegments(varnames![index])
                : namingMagnitude.StartsWith('-') ? $"ValueNeg{namingMagnitude.TrimStart('-')}"
                : $"Value{namingMagnitude}";

            if (!emitted.Add(csharpName))
            {
                var suffix = 2;
                var deduped = $"{csharpName}_{suffix}";
                while (!emitted.Add(deduped))
                {
                    suffix++;
                    deduped = $"{csharpName}_{suffix}";
                }
                csharpName = deduped;
            }

            members.Add(
                new GeneratedEnumMember(
                    csharpName,
                    null,
                    // Raw decimal digits: the string carrier keeps constants above
                    // Int64 (ulong range) byte-for-byte too.
                    memberNode.ToJsonString().Trim()
                )
            );
            index++;
        }

        return new GeneratedEnum(name, members, schema.Format, schema.Description);
    }

    internal static GeneratedBrand MapBrand(string name, IOpenApiSchema schema)
    {
        var brandName = GetExtensionString(schema, "x-rivet-brand") ?? name;
        var innerType = ResolvePrimitiveType(schema) ?? "string";
        return new GeneratedBrand(brandName, innerType, schema.Format, schema.Description);
    }

    // --- Generic type helpers ---

    internal static string BuildGenericTypeString(GenericTemplateInfo info)
    {
        // Build e.g. "PagedResult<TaskDto>" from template name and args
        var argStrings = info.TypeParams.Select(tp =>
            info.Args.TryGetValue(tp, out var concrete) ? concrete : tp
        );
        return $"{info.Name}<{string.Join(", ", argStrings)}>";
    }

    internal static string ReverseSubstituteTypes(
        string csharpType,
        Dictionary<string, string> reverseMap
    )
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

    private static readonly HashSet<string> _schemaNameMaps =
    [
        "properties",
        "patternProperties",
        "$defs",
        "dependentSchemas",
        "mapping",
    ];

    private static readonly HashSet<string> _schemaDataKeywords =
    [
        "const",
        "default",
        "enum",
        "example",
        "examples",
    ];

    /// <summary>
    /// Identity of an inline schema for synthetic-type reuse: its OpenAPI 3.1 serialisation
    /// (<c>$ref</c>s kept as references), canonicalised so that key order and
    /// <c>required</c> order do not split one shape into two types. Vendor extensions are
    /// left out: they do not change the generated type.
    /// </summary>
    internal static string ComputeSchemaFingerprint(IOpenApiSchema schema)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        schema.SerializeAsV31(
            new OpenApiJsonWriter(writer, new OpenApiJsonWriterSettings { Terse = true })
        );
        return Canonical(JsonNode.Parse(writer.ToString()), null)?.ToJsonString() ?? "null";

        static JsonNode? Canonical(JsonNode? node, string? key) =>
            node switch
            {
                _ when key is not null && _schemaDataKeywords.Contains(key) => node?.DeepClone(),
                JsonObject obj => new JsonObject(
                    obj.Where(entry =>
                            key is not null && _schemaNameMaps.Contains(key)
                            || !entry.Key.StartsWith("x-", StringComparison.Ordinal)
                        )
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry =>
                            KeyValuePair.Create(entry.Key, Canonical(entry.Value, entry.Key))
                        )
                ),
                JsonArray array when key == "required" => new JsonArray(
                    array
                        .Select(item => item?.DeepClone())
                        .OrderBy(item => item?.ToJsonString(), StringComparer.Ordinal)
                        .ToArray()
                ),
                JsonArray array => new JsonArray(
                    array.Select(item => Canonical(item, null)).ToArray()
                ),
                _ => node?.DeepClone(),
            };
    }
}
