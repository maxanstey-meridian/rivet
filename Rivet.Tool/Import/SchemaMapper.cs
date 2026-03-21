using System.Text;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps OpenAPI schema objects to C# type representations.
/// Builds a registry of discovered records, enums, and branded value objects.
/// </summary>
internal sealed class SchemaMapper
{
    private static readonly HashSet<string> BrandFormats = ["email", "uri", "url", "uri-reference"];

    private const int MaxRecursionDepth = 50;

    private readonly List<string> _warnings;
    private readonly List<GeneratedRecord> _extraRecords = [];
    private readonly HashSet<string> _resolving = [];
    private readonly Dictionary<string, string> _schemaFingerprints = new();
    private int _syntheticCounter;
    private int _recursionDepth;

    public SchemaMapper(List<string> warnings)
    {
        _warnings = warnings;
    }

    /// <summary>
    /// Records synthesised from inline anonymous objects during type resolution.
    /// </summary>
    public IReadOnlyList<GeneratedRecord> ExtraRecords => _extraRecords;

    /// <summary>
    /// Register a synthetic record (e.g. parameter input records built by ContractBuilder).
    /// </summary>
    public void AddExtraRecord(GeneratedRecord record) => _extraRecords.Add(record);

    /// <summary>
    /// Walk #/components/schemas and return C# type representations.
    /// </summary>
    public SchemaMapResult MapSchemas(IDictionary<string, IOpenApiSchema> schemas)
    {
        var records = new List<GeneratedRecord>();
        var enums = new List<GeneratedEnum>();
        var brands = new List<GeneratedBrand>();
        var usedNames = new HashSet<string>();

        // Pre-scan: collect generic template info from x-rivet-generic extensions
        var genericTemplates = new Dictionary<string, GenericTemplateInfo>();
        var handledByGeneric = new HashSet<string>();

        foreach (var (key, schema) in schemas)
        {
            if (TryGetGenericExtension(schema, out var info))
            {
                if (!genericTemplates.ContainsKey(info!.Name))
                {
                    genericTemplates[info.Name] = info;
                }

                handledByGeneric.Add(key);
            }
        }

        // Emit one generic template record per unique template name
        foreach (var (templateName, info) in genericTemplates)
        {
            var templateRecord = BuildGenericTemplateRecord(templateName, info, schemas);
            if (templateRecord is not null)
            {
                records.Add(templateRecord);
            }
        }

        foreach (var (key, schema) in schemas)
        {
            var name = SanitizeName(key);

            // Deduplicate schema names that collide after PascalCase sanitization
            if (!usedNames.Add(name))
            {
                var suffix = 2;
                while (!usedNames.Add($"{name}_{suffix}"))
                {
                    suffix++;
                }
                name = $"{name}_{suffix}";
            }

            // Skip $ref aliases — these are resolved by the library and handled inline
            if (schema is OpenApiSchemaReference)
            {
                continue;
            }

            // Skip monomorphised schemas handled by generic templates
            if (handledByGeneric.Contains(key))
            {
                continue;
            }

            if (IsStringEnum(schema))
            {
                enums.Add(MapEnum(name, schema));
                continue;
            }

            if (IsBrandedString(schema))
            {
                brands.Add(MapBrand(name, schema));
                continue;
            }

            if (schema.AllOf is { Count: > 0 })
            {
                var record = ResolveAllOfRecord(name, schema.AllOf);
                record = MergeWithSiblingProperties(record, schema, name);

                // Skip empty allOf records — resolved inline via $ref
                if (record.Properties.Count == 0 && schema.Properties is not { Count: > 0 })
                {
                    continue;
                }

                records.Add(record);
                continue;
            }

            if (schema.OneOf is { Count: > 0 })
            {
                // Skip nullable oneOf (exactly 2 items, one null) — handled inline
                if (IsNullableOneOf(schema.OneOf))
                {
                    continue;
                }

                records.Add(ResolveUnionRecord(name, schema.OneOf));
                continue;
            }

            if (schema.AnyOf is { Count: > 1 })
            {
                records.Add(ResolveUnionRecord(name, schema.AnyOf));
                continue;
            }

            if (IsObject(schema))
            {
                // Object with no properties → resolved inline as Dictionary, not as a record
                if (schema.Properties is not { Count: > 0 })
                {
                    continue;
                }

                records.Add(MapRecord(name, schema));
                continue;
            }

            // Primitive aliases (e.g. { "type": "string", "format": "date-time" }) — skip, resolved inline
        }

        return new SchemaMapResult(records, enums, brands);
    }

    /// <summary>
    /// Resolve an OpenAPI schema to a C# type string.
    /// </summary>
    public string ResolveCSharpType(IOpenApiSchema schema, string? context = null)
    {
        if (++_recursionDepth > MaxRecursionDepth)
        {
            _recursionDepth--;
            return "System.Text.Json.JsonElement";
        }

        try
        {
            return ResolveCSharpTypeCore(schema, context);
        }
        finally
        {
            _recursionDepth--;
        }
    }

    private string ResolveCSharpTypeCore(IOpenApiSchema schema, string? context)
    {
        // $ref — library resolves refs, but OpenApiSchemaReference preserves the reference name
        if (schema is OpenApiSchemaReference schemaRef)
        {
            // If the target is a property-less object schema, resolve to Dictionary
            // (no record was generated for it in MapSchemas)
            if (IsObject(schemaRef) && schemaRef.Properties is not { Count: > 0 })
            {
                return ResolveObjectType(schemaRef, context);
            }

            // If the target has x-rivet-generic, resolve to generic type string
            if (TryGetGenericExtension(schemaRef, out var genericInfo))
            {
                return BuildGenericTypeString(genericInfo!);
            }

            // If the target would generate a type (record, enum, brand), use the ref name.
            // Otherwise it's a primitive alias — fall through to resolve the underlying type.
            if (WouldGenerateType(schemaRef))
            {
                return SanitizeName(schemaRef.Reference.Id!);
            }
        }

        var type = schema.Type;

        // Nullable type: Type flags contain Null alongside another type (e.g. ["string", "null"])
        if (type.HasValue && type.Value.HasFlag(JsonSchemaType.Null))
        {
            var nonNullType = type.Value & ~JsonSchemaType.Null;
            if (nonNullType != 0)
            {
                var innerType = ResolveSingleType(nonNullType, schema, context);
                return innerType + "?";
            }

            // Pure null type — check for 3.0 nullable composition (allOf [$ref] + nullable: true)
            if (type.Value == JsonSchemaType.Null)
            {
                if (schema.AllOf is { Count: 1 }
                    && schema.AllOf[0] is OpenApiSchemaReference nullableRef
                    && schema.Properties is not { Count: > 0 })
                {
                    return SanitizeName(nullableRef.Reference.Id!) + "?";
                }

                if (schema.AllOf is { Count: > 0 })
                {
                    var allOfName = Naming.CapIdentifierLength(context ?? $"Composed{++_syntheticCounter}");
                    var record = ResolveAllOfRecord(allOfName, schema.AllOf);
                    record = MergeWithSiblingProperties(record, schema, allOfName);
                    _extraRecords.Add(record);
                    return allOfName + "?";
                }

                return "System.Text.Json.JsonElement";
            }
        }

        // Single type (non-null)
        if (type.HasValue && type.Value != 0)
        {
            return ResolveSingleType(type.Value, schema, context);
        }

        // No type specified — check composition keywords

        // oneOf with null (nullable ref in 3.1)
        if (schema.OneOf is { Count: 2 })
        {
            IOpenApiSchema? refPart = null;
            var hasNull = false;

            foreach (var item in schema.OneOf)
            {
                if (item.Type.HasValue && item.Type.Value == JsonSchemaType.Null)
                {
                    hasNull = true;
                }
                else
                {
                    refPart = item;
                }
            }

            if (hasNull && refPart is not null)
            {
                var resolved = ResolveCSharpType(refPart, context);
                return resolved.EndsWith("?") ? resolved : resolved + "?";
            }
        }

        // anyOf with single element + nullable (OpenAPI 3.0 nullable pattern)
        if (schema.AnyOf is { Count: 1 })
        {
            var inner = schema.AnyOf[0];
            var resolved = ResolveCSharpType(inner, context);
            return resolved;
        }

        // allOf inline → synthetic flattened record
        if (schema.AllOf is { Count: > 0 })
        {
            // Short-circuit: allOf with a single $ref and no sibling properties → just use the named type
            if (schema.AllOf.Count == 1
                && schema.AllOf[0] is OpenApiSchemaReference singleRef
                && schema.Properties is not { Count: > 0 })
            {
                return SanitizeName(singleRef.Reference.Id!);
            }

            var allOfName = Naming.CapIdentifierLength(context ?? $"Composed{++_syntheticCounter}");
            var record = ResolveAllOfRecord(allOfName, schema.AllOf);
            record = MergeWithSiblingProperties(record, schema, allOfName);

            _extraRecords.Add(record);
            return allOfName;
        }

        // oneOf multi-element (non-nullable) → synthetic union wrapper
        if (schema.OneOf is { Count: > 0 })
        {
            if (context is not null)
            {
                var record = ResolveUnionRecord(context, schema.OneOf);
                _extraRecords.Add(record);
                return context;
            }

            var syntheticOneOf = $"Composed{++_syntheticCounter}";
            var syntheticOneOfRecord = ResolveUnionRecord(syntheticOneOf, schema.OneOf);
            _extraRecords.Add(syntheticOneOfRecord);
            return syntheticOneOf;
        }

        // anyOf multi-element → same as oneOf
        if (schema.AnyOf is { Count: > 1 })
        {
            if (context is not null)
            {
                var record = ResolveUnionRecord(context, schema.AnyOf);
                _extraRecords.Add(record);
                return context;
            }

            var syntheticAnyOf = $"Composed{++_syntheticCounter}";
            var syntheticAnyOfRecord = ResolveUnionRecord(syntheticAnyOf, schema.AnyOf);
            _extraRecords.Add(syntheticAnyOfRecord);
            return syntheticAnyOf;
        }

        // enum without explicit type (common in some generators)
        if (schema.Enum is { Count: > 0 })
        {
            return "string";
        }

        // Inline object with properties but no type field (JSON Schema: properties implies object)
        if (schema.Properties is { Count: > 0 })
        {
            return ResolveObjectType(schema, context);
        }

        // const without type — infer from the const value
        if (schema.Const is not null)
        {
            var constStr = schema.Const;
            if (bool.TryParse(constStr, out _))
            {
                return "bool";
            }

            if (int.TryParse(constStr, out _))
            {
                return "int";
            }

            if (double.TryParse(constStr, out _))
            {
                return "double";
            }

            return "string";
        }

        // Final fallback — only warn if the schema had structural properties we should have handled
        if (HasResolvableProperties(schema))
        {
            return WarnAndFallback("Schema could not be resolved to a C# type");
        }

        return "System.Text.Json.JsonElement";
    }

    private string ResolveSingleType(JsonSchemaType type, IOpenApiSchema schema, string? context)
    {
        // x-rivet-csharp-type takes precedence — exact C# type for lossless round-trips
        var csharpType = GetExtensionString(schema, "x-rivet-csharp-type");
        if (csharpType is not null)
        {
            return csharpType;
        }

        if (type.HasFlag(JsonSchemaType.String))
        {
            return ResolveStringType(schema);
        }

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            return ResolveIntegerType(schema);
        }

        if (type.HasFlag(JsonSchemaType.Number))
        {
            return ResolveNumberType(schema);
        }

        if (type.HasFlag(JsonSchemaType.Boolean))
        {
            return "bool";
        }

        if (type.HasFlag(JsonSchemaType.Array))
        {
            return ResolveArrayType(schema, context);
        }

        if (type.HasFlag(JsonSchemaType.Object))
        {
            return ResolveObjectType(schema, context);
        }

        return WarnAndFallback($"Unsupported JSON Schema type '{type}'");
    }

    private GeneratedRecord ResolveAllOfRecord(string name, IList<IOpenApiSchema> allOfList, HashSet<string>? visited = null)
    {
        if (!_resolving.Add(name))
        {
            return new GeneratedRecord(name, []);
        }

        visited ??= [];
        visited.Add(name);

        var merged = new List<RecordProperty>();
        var seenNames = new HashSet<string>();

        foreach (var element in allOfList)
        {
            List<RecordProperty> props;

            if (element is OpenApiSchemaReference elementRef)
            {
                var refName = SanitizeName(elementRef.Reference.Id!);

                if (visited.Contains(refName))
                {
                    continue;
                }

                // If the ref target itself has allOf, recurse
                if (element.AllOf is { Count: > 0 })
                {
                    var nested = ResolveAllOfRecord(refName, element.AllOf, visited);
                    props = nested.Properties.ToList();
                }
                else
                {
                    props = ExtractProperties(element, name);
                }
            }
            else
            {
                props = ExtractProperties(element, name);
            }

            foreach (var prop in props)
            {
                if (seenNames.Add(prop.Name))
                {
                    merged.Add(prop);
                }
            }
        }

        _resolving.Remove(name);
        return new GeneratedRecord(name, merged);
    }

    private GeneratedRecord ResolveUnionRecord(string name, IList<IOpenApiSchema> variants)
    {
        var properties = new List<RecordProperty>();
        var optionIndex = 0;

        foreach (var variant in variants)
        {
            if (variant is OpenApiSchemaReference variantRef && WouldGenerateType(variantRef))
            {
                var refName = SanitizeName(variantRef.Reference.Id!);
                properties.Add(new RecordProperty($"As{refName}", $"{refName}?", false));
            }
            else if (variant.Properties is { Count: > 0 })
            {
                // Inline object → synthesize sub-record
                var subName = $"{name}Option{optionIndex}";
                var record = MapRecord(subName, variant);
                _extraRecords.Add(record);
                properties.Add(new RecordProperty($"As{subName}", $"{subName}?", false));
                optionIndex++;
            }
            else
            {
                // Inline primitive
                var csharpType = ResolveCSharpType(variant);
                var nullableType = csharpType.EndsWith("?") ? csharpType : $"{csharpType}?";
                var typeName = PrimitiveDisplayName(csharpType.TrimEnd('?'));
                properties.Add(new RecordProperty($"As{typeName}", nullableType, false));
            }
        }

        return new GeneratedRecord(name, DeduplicateProperties(properties));
    }

    private List<RecordProperty> ExtractProperties(IOpenApiSchema schema, string context)
    {
        var properties = new List<RecordProperty>();
        var requiredSet = schema.Required ?? (ISet<string>)new HashSet<string>();

        if (schema.Properties is not null)
        {
            foreach (var (propKey, propSchema) in schema.Properties)
            {
                var propName = Naming.ToPascalCaseFromSegments(propKey);
                if (propName == context)
                {
                    propName += "Value";
                }

                var propContext = Naming.CapIdentifierLength($"{context}{propName}");
                var isRequired = requiredSet.Contains(propKey);
                var csharpType = ResolveCSharpType(propSchema, propContext);

                if (!isRequired && !csharpType.EndsWith("?"))
                {
                    csharpType += "?";
                }

                var isDeprecated = propSchema.Deprecated;
                properties.Add(new RecordProperty(propName, csharpType, isRequired, isDeprecated));
            }
        }

        return DeduplicateProperties(properties);
    }

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

    private GeneratedRecord MergeWithSiblingProperties(
        GeneratedRecord record, IOpenApiSchema schema, string name)
    {
        if (schema.Properties is not { Count: > 0 })
        {
            return record;
        }

        var siblingProps = ExtractProperties(schema, name);
        var merged = record.Properties.ToList();
        var seen = new HashSet<string>(merged.Select(p => p.Name));
        foreach (var prop in siblingProps)
        {
            if (seen.Add(prop.Name))
            {
                merged.Add(prop);
            }
        }

        return new GeneratedRecord(name, merged);
    }

    private static string PrimitiveDisplayName(string csharpType)
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

    private static bool IsNullableOneOf(IList<IOpenApiSchema> oneOfList)
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

    private static string SanitizeName(string name)
    {
        return Naming.ToPascalCaseFromSegments(name);
    }

    /// <summary>
    /// Computes a structural fingerprint for an inline schema.
    /// </summary>
    private static string ComputeSchemaFingerprint(IOpenApiSchema schema)
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

    /// <summary>
    /// Returns true if MapSchemas would generate a record, enum, or brand for this schema.
    /// </summary>
    private static bool WouldGenerateType(IOpenApiSchema schema)
    {
        if (IsStringEnum(schema))
        {
            return true;
        }

        if (IsBrandedString(schema))
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

        return false;
    }

    private static bool HasResolvableProperties(IOpenApiSchema schema)
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

    private string WarnAndFallback(string reason)
    {
        _warnings.Add($"{reason} — mapped to 'JsonElement'.");
        return "System.Text.Json.JsonElement";
    }

    private static string ResolveStringType(IOpenApiSchema schema)
    {
        if (schema.Format is "binary" || HasExtension(schema, "x-rivet-file"))
        {
            return "IFormFile";
        }

        return schema.Format switch
        {
            "date-time" => "DateTime",
            "date" => "DateOnly",
            "guid" or "uuid" => "Guid",
            _ => "string",
        };
    }

    private static string ResolveIntegerType(IOpenApiSchema schema)
    {
        return schema.Format == "int64" ? "long" : "int";
    }

    private static string ResolveNumberType(IOpenApiSchema schema)
    {
        return schema.Format switch
        {
            "float" => "float",
            "decimal" => "decimal",
            _ => "double",
        };
    }

    private string ResolveArrayType(IOpenApiSchema schema, string? context)
    {
        if (schema.Items is not null)
        {
            var itemType = ResolveCSharpType(schema.Items, context);
            return $"List<{itemType}>";
        }

        return $"List<{WarnAndFallback("Array schema missing 'items'")}>";
    }

    private string ResolveObjectType(IOpenApiSchema schema, string? context)
    {
        if (schema.AdditionalProperties is not null)
        {
            var valueType = ResolveCSharpType(schema.AdditionalProperties, context);
            return $"Dictionary<string, {valueType}>";
        }

        // Inline object with properties
        if (schema.Properties is { Count: > 0 })
        {
            var fingerprint = ComputeSchemaFingerprint(schema);
            if (_schemaFingerprints.TryGetValue(fingerprint, out var existingName))
            {
                return existingName;
            }

            var name = context ?? $"Synthetic{++_syntheticCounter}";
            var record = MapRecord(name, schema);
            _extraRecords.Add(record);
            _schemaFingerprints[fingerprint] = name;
            return name;
        }

        // Bare object with no properties or additionalProperties → untyped map
        return "Dictionary<string, System.Text.Json.JsonElement>";
    }

    private static bool IsStringEnum(IOpenApiSchema schema)
    {
        return schema.Type.HasValue
            && schema.Type.Value.HasFlag(JsonSchemaType.String)
            && schema.Enum is { Count: > 0 };
    }

    private static bool IsBrandedString(IOpenApiSchema schema)
    {
        if (!schema.Type.HasValue || !schema.Type.Value.HasFlag(JsonSchemaType.String))
        {
            return false;
        }

        // x-rivet-brand extension takes precedence
        if (HasExtension(schema, "x-rivet-brand"))
        {
            return true;
        }

        return schema.Format is not null && BrandFormats.Contains(schema.Format);
    }

    private static bool IsObject(IOpenApiSchema schema)
    {
        if (schema.Type.HasValue && schema.Type.Value.HasFlag(JsonSchemaType.Object))
        {
            return true;
        }

        return !schema.Type.HasValue && schema.Properties is { Count: > 0 };
    }

    private GeneratedRecord MapRecord(string name, IOpenApiSchema schema)
    {
        if (!_resolving.Add(name))
        {
            return new GeneratedRecord(name, []);
        }

        var properties = ExtractProperties(schema, name);
        _resolving.Remove(name);
        return new GeneratedRecord(name, properties);
    }

    private static GeneratedEnum MapEnum(string name, IOpenApiSchema schema)
    {
        var seen = new Dictionary<string, int>();
        var members = new List<string>();
        foreach (var member in schema.Enum!)
        {
            if (member is null)
            {
                continue;
            }

            var sanitized = Naming.ToPascalCaseFromSegments(member.ToString());
            if (seen.TryGetValue(sanitized, out var count))
            {
                count++;
                seen[sanitized] = count;
                members.Add($"{sanitized}_{count}");
            }
            else
            {
                seen[sanitized] = 1;
                members.Add(sanitized);
            }
        }

        return new GeneratedEnum(name, members);
    }

    private static GeneratedBrand MapBrand(string name, IOpenApiSchema schema)
    {
        var brandName = GetExtensionString(schema, "x-rivet-brand") ?? name;
        var innerType = ResolvePrimitiveType(schema) ?? "string";
        return new GeneratedBrand(brandName, innerType);
    }

    private static string? ResolvePrimitiveType(IOpenApiSchema schema)
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

    private static bool HasExtension(IOpenApiSchema schema, string key)
    {
        return schema.Extensions is not null && schema.Extensions.ContainsKey(key);
    }

    private static string? GetExtensionString(IOpenApiSchema schema, string key)
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

    private static bool TryGetGenericExtension(IOpenApiSchema schema, out GenericTemplateInfo? info)
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

    private GeneratedRecord? BuildGenericTemplateRecord(
        string templateName,
        GenericTemplateInfo info,
        IDictionary<string, IOpenApiSchema> schemas)
    {
        // Find the first monomorphised instance to derive the template properties
        IOpenApiSchema? firstInstance = null;
        foreach (var (key, schema) in schemas)
        {
            if (TryGetGenericExtension(schema, out var schemaInfo) && schemaInfo!.Name == templateName)
            {
                firstInstance = schema;
                break;
            }
        }

        if (firstInstance is null)
        {
            return null;
        }

        // Extract properties from the monomorphised instance
        var monoProps = ExtractProperties(firstInstance, templateName);

        // Reverse-substitute concrete types back to type params using the args map
        var reverseMap = info.Args.ToDictionary(kv => kv.Value, kv => kv.Key);
        var templateProps = new List<RecordProperty>();
        foreach (var prop in monoProps)
        {
            var templatedType = ReverseSubstituteTypes(prop.CSharpType, reverseMap);
            templateProps.Add(prop with { CSharpType = templatedType });
        }

        return new GeneratedRecord(templateName, templateProps, info.TypeParams);
    }

    private static string BuildGenericTypeString(GenericTemplateInfo info)
    {
        // Build e.g. "PagedResult<TaskDto>" from template name and args
        var argStrings = info.TypeParams.Select(tp =>
            info.Args.TryGetValue(tp, out var concrete) ? concrete : tp);
        return $"{info.Name}<{string.Join(", ", argStrings)}>";
    }

    private static string ReverseSubstituteTypes(string csharpType, Dictionary<string, string> reverseMap)
    {
        // Replace concrete type names with type parameter names
        // Work from longest keys first to avoid partial replacements
        var result = csharpType;
        foreach (var (concreteType, typeParam) in reverseMap.OrderByDescending(kv => kv.Key.Length))
        {
            result = result.Replace(concreteType, typeParam);
        }

        return result;
    }
}

// --- Intermediate types ---

internal sealed record GenericTemplateInfo(
    string Name,
    IReadOnlyList<string> TypeParams,
    Dictionary<string, string> Args);


internal sealed record SchemaMapResult(
    IReadOnlyList<GeneratedRecord> Records,
    IReadOnlyList<GeneratedEnum> Enums,
    IReadOnlyList<GeneratedBrand> Brands);

internal sealed record GeneratedRecord(
    string Name,
    IReadOnlyList<RecordProperty> Properties,
    IReadOnlyList<string>? TypeParameters = null);

internal sealed record RecordProperty(
    string Name,
    string CSharpType,
    bool IsRequired,
    bool IsDeprecated = false);

internal sealed record GeneratedEnum(
    string Name,
    IReadOnlyList<string> Members);

internal sealed record GeneratedBrand(
    string Name,
    string InnerType);
