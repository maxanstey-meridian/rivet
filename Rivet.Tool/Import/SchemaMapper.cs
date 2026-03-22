using Microsoft.OpenApi;

namespace Rivet.Tool.Import;

/// <summary>
/// Maps OpenAPI schema objects to C# type representations.
/// Builds a registry of discovered records, enums, and branded value objects.
/// </summary>
internal sealed class SchemaMapper
{
    private const int MaxRecursionDepth = 50;

    private readonly ResolutionContext _ctx;
    private readonly RecordSynthesizer _synth;

    public SchemaMapper(List<string> warnings)
    {
        _ctx = new ResolutionContext(warnings);
        _synth = new RecordSynthesizer(_ctx, ResolveCSharpType);
    }

    /// <summary>
    /// Records synthesised from inline anonymous objects during type resolution.
    /// </summary>
    public IReadOnlyList<GeneratedRecord> ExtraRecords => _ctx.ExtraRecords;

    /// <summary>
    /// Enums synthesised from inline enum properties during type resolution.
    /// </summary>
    public IReadOnlyList<GeneratedEnum> ExtraEnums => _ctx.ExtraEnums;

    /// <summary>
    /// Register a synthetic record (e.g. parameter input records built by ContractBuilder).
    /// </summary>
    public void AddExtraRecord(GeneratedRecord record) => _ctx.ExtraRecords.Add(record);

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
            if (SchemaClassifier.TryGetGenericExtension(schema, out var info))
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
            var templateRecord = _synth.BuildGenericTemplateRecord(templateName, info, schemas);
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

            // Track mapping from original OpenAPI key to (possibly deduped) C# name
            _ctx.SchemaNameMap[key] = name;

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

            if (SchemaClassifier.IsStringEnum(schema))
            {
                enums.Add(SchemaClassifier.MapEnum(name, schema));
                continue;
            }

            if (SchemaClassifier.IsBrand(schema))
            {
                brands.Add(SchemaClassifier.MapBrand(name, schema));
                continue;
            }

            if (schema.AllOf is { Count: > 0 })
            {
                var record = _synth.ResolveAllOfRecord(name, schema.AllOf);
                record = _synth.MergeWithSiblingProperties(record, schema, name);

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
                if (SchemaClassifier.IsNullableOneOf(schema.OneOf))
                {
                    continue;
                }

                records.Add(_synth.ResolveUnionRecord(name, schema.OneOf));
                continue;
            }

            if (schema.AnyOf is { Count: > 1 })
            {
                records.Add(_synth.ResolveUnionRecord(name, schema.AnyOf));
                continue;
            }

            if (SchemaClassifier.IsObject(schema))
            {
                // Object with no properties → resolved inline as Dictionary, not as a record
                // Unless marked with x-rivet-empty-record extension
                if (schema.Properties is not { Count: > 0 })
                {
                    if (SchemaClassifier.HasExtension(schema, "x-rivet-empty-record"))
                    {
                        records.Add(new GeneratedRecord(name, []));
                    }
                    continue;
                }

                records.Add(_synth.MapRecord(name, schema));
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
        if (++_ctx.RecursionDepth > MaxRecursionDepth)
        {
            _ctx.RecursionDepth--;
            return "System.Text.Json.JsonElement";
        }

        try
        {
            return ResolveCSharpTypeCore(schema, context);
        }
        finally
        {
            _ctx.RecursionDepth--;
        }
    }

    private string ResolveCSharpTypeCore(IOpenApiSchema schema, string? context)
    {
        // $ref — try to resolve directly; if it's a primitive alias, fall through to type resolution
        if (schema is OpenApiSchemaReference schemaRef
            && TryResolveSchemaReference(schemaRef, context, out var refResult))
        {
            return refResult;
        }

        if (TryResolveNullableType(schema, context, out var result))
        {
            return result;
        }

        if (schema.Type is { } type && type != 0)
        {
            return ResolveSingleType(type, schema, context);
        }

        if (TryResolveNullableOneOf(schema, context, out result))
        {
            return result;
        }

        if (TryResolveComposition(schema, context, out result))
        {
            return result;
        }

        return ResolveFallbackType(schema, context);
    }

    // --- Resolution dispatch methods (order matters — earlier branches take precedence) ---

    private bool TryResolveSchemaReference(OpenApiSchemaReference schemaRef, string? context, out string result)
    {
        result = "";

        // If the target is a property-less object schema, resolve to Dictionary
        // (no record was generated for it in MapSchemas) — unless marked as empty record
        if (SchemaClassifier.IsObject(schemaRef) && schemaRef.Properties is not { Count: > 0 }
            && !SchemaClassifier.HasExtension(schemaRef, "x-rivet-empty-record"))
        {
            result = ResolveObjectType(schemaRef, context);
            return true;
        }

        // If the target has x-rivet-generic, resolve to generic type string
        if (SchemaClassifier.TryGetGenericExtension(schemaRef, out var genericInfo))
        {
            result = SchemaClassifier.BuildGenericTypeString(genericInfo!);
            return true;
        }

        // If the target would generate a type (record, enum, brand), use the ref name.
        // Otherwise it's a primitive alias — fall through to resolve the underlying type.
        if (SchemaClassifier.WouldGenerateType(schemaRef))
        {
            result = SanitizeName(schemaRef.Reference.Id!);
            return true;
        }

        // Primitive alias — fall through to type resolution on the resolved schema
        return false;
    }

    private bool TryResolveNullableType(IOpenApiSchema schema, string? context, out string result)
    {
        result = "";
        var type = schema.Type;

        if (!type.HasValue || !type.Value.HasFlag(JsonSchemaType.Null))
        {
            return false;
        }

        var nonNullType = type.Value & ~JsonSchemaType.Null;
        if (nonNullType != 0)
        {
            result = ResolveSingleType(nonNullType, schema, context) + "?";
            return true;
        }

        // Pure null type — check for 3.0 nullable composition (allOf [$ref] + nullable: true)
        if (type.Value == JsonSchemaType.Null)
        {
            if (schema.AllOf is { Count: 1 }
                && schema.AllOf[0] is OpenApiSchemaReference nullableRef
                && schema.Properties is not { Count: > 0 })
            {
                result = SanitizeName(nullableRef.Reference.Id!) + "?";
                return true;
            }

            if (schema.AllOf is { Count: > 0 })
            {
                var allOfName = context ?? _ctx.NextSyntheticName("Composed");
                var record = _synth.ResolveAllOfRecord(allOfName, schema.AllOf);
                record = _synth.MergeWithSiblingProperties(record, schema, allOfName);
                _ctx.ExtraRecords.Add(record);
                result = allOfName + "?";
                return true;
            }

            // x-rivet-csharp-type on nullable untyped schema
            var nullableCsharpType = SchemaClassifier.GetExtensionString(schema, "x-rivet-csharp-type");
            if (nullableCsharpType is not null)
            {
                result = SchemaClassifier.ResolveJsonNodeFqn(nullableCsharpType) + "?";
                return true;
            }

            result = "System.Text.Json.JsonElement";
            return true;
        }

        return false;
    }

    private bool TryResolveNullableOneOf(IOpenApiSchema schema, string? context, out string result)
    {
        result = "";

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
                result = resolved.EndsWith("?") ? resolved : resolved + "?";
                return true;
            }
        }

        // anyOf with single element (OpenAPI 3.0 nullable pattern)
        if (schema.AnyOf is { Count: 1 })
        {
            result = ResolveCSharpType(schema.AnyOf[0], context);
            return true;
        }

        return false;
    }

    private bool TryResolveComposition(IOpenApiSchema schema, string? context, out string result)
    {
        result = "";

        // allOf inline → synthetic flattened record
        if (schema.AllOf is { Count: > 0 })
        {
            // Short-circuit: allOf with a single $ref and no sibling properties → just use the named type
            if (schema.AllOf.Count == 1
                && schema.AllOf[0] is OpenApiSchemaReference singleRef
                && schema.Properties is not { Count: > 0 })
            {
                result = SanitizeName(singleRef.Reference.Id!);
                return true;
            }

            var allOfName = context ?? _ctx.NextSyntheticName("Composed");
            var record = _synth.ResolveAllOfRecord(allOfName, schema.AllOf);
            record = _synth.MergeWithSiblingProperties(record, schema, allOfName);
            _ctx.ExtraRecords.Add(record);
            result = allOfName;
            return true;
        }

        // oneOf multi-element (non-nullable) → synthetic union wrapper
        if (schema.OneOf is { Count: > 0 })
        {
            result = ResolveUnionType(context, schema.OneOf);
            return true;
        }

        // anyOf multi-element → same as oneOf
        if (schema.AnyOf is { Count: > 1 })
        {
            result = ResolveUnionType(context, schema.AnyOf);
            return true;
        }

        return false;
    }

    private string ResolveUnionType(string? context, IList<IOpenApiSchema> variants)
    {
        var name = context ?? _ctx.NextSyntheticName("Composed");
        var record = _synth.ResolveUnionRecord(name, variants);
        _ctx.ExtraRecords.Add(record);
        return name;
    }

    private string ResolveFallbackType(IOpenApiSchema schema, string? context)
    {
        // enum without explicit type (common in some generators)
        if (schema.Enum is { Count: > 1 })
        {
            return SynthesizeInlineEnum(schema, context);
        }

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
            return ResolveConstType(schema.Const);
        }

        // x-rivet-csharp-type on untyped schemas (e.g. JsonNode, JsonObject, JsonArray)
        var untypedCsharpType = SchemaClassifier.GetExtensionString(schema, "x-rivet-csharp-type");
        if (untypedCsharpType is not null)
        {
            return SchemaClassifier.ResolveJsonNodeFqn(untypedCsharpType);
        }

        // Final fallback — only warn if the schema had structural properties we should have handled
        if (SchemaClassifier.HasResolvableProperties(schema))
        {
            return WarnAndFallback("Schema could not be resolved to a C# type");
        }

        return "System.Text.Json.JsonElement";
    }

    private static string ResolveConstType(string constValue)
    {
        if (bool.TryParse(constValue, out _))
        {
            return "bool";
        }

        if (int.TryParse(constValue, out _))
        {
            return "int";
        }

        if (double.TryParse(constValue, out _))
        {
            return "double";
        }

        return "string";
    }

    // --- Type resolution helpers ---

    private string ResolveSingleType(JsonSchemaType type, IOpenApiSchema schema, string? context)
    {
        // x-rivet-csharp-type takes precedence — exact C# type for lossless round-trips
        var csharpType = SchemaClassifier.GetExtensionString(schema, "x-rivet-csharp-type");
        if (csharpType is not null)
        {
            return SchemaClassifier.ResolveJsonNodeFqn(csharpType);
        }

        if (type.HasFlag(JsonSchemaType.String))
        {
            if (schema.Enum is { Count: > 1 })
            {
                return SynthesizeInlineEnum(schema, context);
            }

            return SchemaClassifier.ResolveStringType(schema);
        }

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            return SchemaClassifier.ResolveIntegerType(schema);
        }

        if (type.HasFlag(JsonSchemaType.Number))
        {
            return SchemaClassifier.ResolveNumberType(schema);
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

    private string SanitizeName(string name)
    {
        if (_ctx.SchemaNameMap.TryGetValue(name, out var mapped))
        {
            return mapped;
        }

        return Naming.ToPascalCaseFromSegments(name);
    }

    private string WarnAndFallback(string reason)
    {
        _ctx.Warnings.Add($"{reason} — mapped to 'JsonElement'.");
        return "System.Text.Json.JsonElement";
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

    private string SynthesizeInlineEnum(IOpenApiSchema schema, string? context)
    {
        var fingerprint = SchemaClassifier.ComputeSchemaFingerprint(schema);
        if (_ctx.SchemaFingerprints.TryGetValue(fingerprint, out var existingName))
        {
            return existingName;
        }

        var name = context ?? _ctx.NextSyntheticName("Enum");
        var enumDef = SchemaClassifier.MapEnum(name, schema);
        _ctx.ExtraEnums.Add(enumDef);
        _ctx.SchemaFingerprints[fingerprint] = name;
        return name;
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
            var fingerprint = SchemaClassifier.ComputeSchemaFingerprint(schema);
            if (_ctx.SchemaFingerprints.TryGetValue(fingerprint, out var existingName))
            {
                return existingName;
            }

            var name = context ?? $"Synthetic{++_ctx.SyntheticCounter}";
            var record = _synth.MapRecord(name, schema);
            _ctx.ExtraRecords.Add(record);
            _ctx.SchemaFingerprints[fingerprint] = name;
            return name;
        }

        // Bare object with no properties or additionalProperties → untyped map
        return "Dictionary<string, System.Text.Json.JsonElement>";
    }
}
