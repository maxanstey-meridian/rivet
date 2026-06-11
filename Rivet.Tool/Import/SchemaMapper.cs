using System.Text.Json.Nodes;
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

    // I1: component alias resolution ("Alias": {"$ref": "#/components/schemas/Real"}).
    // Alias keys map to their FINAL (non-reference) target key; cyclic/missing chains are
    // recorded separately so consumers fall back loudly instead of overflowing the stack
    // chasing the library's reference proxies.
    private readonly Dictionary<string, string> _aliasTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvableAliases = new(StringComparer.Ordinal);
    private IDictionary<string, IOpenApiSchema>? _componentSchemas;

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
    /// <summary>
    /// Dedup-with-shape-check (I3 guard): identical shape reuses the existing record;
    /// a name collision with a different shape gets a suffixed name. Returns the name to reference.
    /// </summary>
    public string AddExtraRecord(GeneratedRecord record) => _ctx.AddOrReuseExtraRecord(record);

    /// <summary>
    /// Check if a type name was already mapped from components/schemas.
    /// </summary>
    public bool HasMappedSchema(string name) => _ctx.SchemaNameMap.ContainsValue(name);

    /// <summary>
    /// True when a components/schemas RECORD with this name exists AND its shape
    /// (property names, C# types, required-ness) matches the candidate properties.
    /// Name-only matches return false — callers must disambiguate instead of reusing.
    /// </summary>
    public bool HasMappedSchemaWithShape(string name, IReadOnlyList<RecordProperty> properties)
    {
        if (!_ctx.MappedComponentRecords.TryGetValue(name, out var record)
            || record.Properties.Count != properties.Count)
        {
            return false;
        }

        var byName = record.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var prop in properties)
        {
            if (!byName.TryGetValue(prop.Name, out var existing)
                || existing.CSharpType != prop.CSharpType
                || existing.IsRequired != prop.IsRequired)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds an existing numbered components/schemas variant (<c>{baseName}2</c>,
    /// <c>{baseName}3</c>, …) whose shape matches exactly, lowest suffix first.
    /// A prior emit∘import loop may already have disambiguated a synthesized input to a
    /// numbered name — reusing it (instead of minting a fresh suffix every loop) keeps
    /// emit∘import a fixed point (GAP-2, I3 residual). Null when nothing matches.
    /// </summary>
    public string? FindNumberedSchemaWithShape(string baseName, IReadOnlyList<RecordProperty> properties)
    {
        return _ctx.MappedComponentRecords.Keys
            .Select(name => (Name: name, Suffix: ParseNumberedSuffix(name, baseName)))
            .Where(entry => entry.Suffix is not null)
            .OrderBy(entry => entry.Suffix!.Value)
            .Where(entry => HasMappedSchemaWithShape(entry.Name, properties))
            .Select(entry => entry.Name)
            .FirstOrDefault();
    }

    private static int? ParseNumberedSuffix(string name, string baseName)
    {
        if (name.Length <= baseName.Length
            || !name.StartsWith(baseName, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(name.AsSpan(baseName.Length), out var suffix) ? suffix : null;
    }

    /// <summary>
    /// Finds an already-mapped record (component or synthesized extra) by its C# name.
    /// Used by ContractBuilder to merge path/query parameters into a body-derived input
    /// record (I14) — null when the name does not resolve to a plain record.
    /// </summary>
    public GeneratedRecord? FindRecordByName(string name)
    {
        if (_ctx.MappedComponentRecords.TryGetValue(name, out var record))
        {
            return record;
        }

        return _ctx.ExtraRecords.FirstOrDefault(r => r.Name == name);
    }

    /// <summary>
    /// Walk #/components/schemas and return C# type representations.
    /// </summary>
    public SchemaMapResult MapSchemas(IDictionary<string, IOpenApiSchema> schemas)
    {
        var records = new List<GeneratedRecord>();
        var enums = new List<GeneratedEnum>();
        var brands = new List<GeneratedBrand>();
        var usedNames = new HashSet<string>();

        // I1: resolve alias chains first, using raw reference ids only (never proxied
        // members — a cyclic alias would overflow the stack inside the library's proxy)
        _componentSchemas = schemas;
        ResolveAliasTargets(schemas);

        // Pre-scan: collect generic template info from x-rivet-generic extensions
        var genericTemplates = new Dictionary<string, GenericTemplateInfo>();
        var handledByGeneric = new HashSet<string>();

        foreach (var (key, schema) in schemas)
        {
            // I1: alias entries are resolved via _aliasTargets; touching their proxied
            // members here would recurse on cyclic chains
            if (schema is OpenApiSchemaReference)
            {
                continue;
            }

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
                _ctx.ReservedTypeNames.Add(templateRecord.Name);
            }
        }

        // Pre-pass: claim every component schema's C# name BEFORE any type resolution runs,
        // so synthetic records/enums created during resolution can never reuse a component
        // name with a different shape (I3 — two types in one Types/{Name}.cs file).
        foreach (var (key, schema) in schemas)
        {
            // I1: aliases produce no file of their own and must NOT claim a name — they
            // map to their target's name in the follow-up loop below
            if (schema is OpenApiSchemaReference)
            {
                continue;
            }

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
            _ctx.ReservedTypeNames.Add(name);
        }

        // I1: alias keys map to the FINAL target's mapped name so every consumer of the
        // alias resolves to a type that actually exists. Unresolvable aliases (cycles,
        // missing targets) get no mapping — their consumers fall back loudly.
        foreach (var (key, schema) in schemas)
        {
            if (schema is not OpenApiSchemaReference)
            {
                continue;
            }

            if (_aliasTargets.TryGetValue(key, out var finalKey)
                && _ctx.SchemaNameMap.TryGetValue(finalKey, out var targetName))
            {
                _ctx.SchemaNameMap[key] = targetName;
            }
        }

        foreach (var (key, schema) in schemas)
        {
            // Skip $ref aliases — resolved via the alias-target map (I1); unresolvable
            // aliases have no SchemaNameMap entry at all
            if (schema is OpenApiSchemaReference)
            {
                continue;
            }

            var name = _ctx.SchemaNameMap[key];

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

            if (SchemaClassifier.IsIntEnum(schema))
            {
                enums.Add(SchemaClassifier.MapIntEnum(name, schema));
                continue;
            }

            if (SchemaClassifier.IsBrand(schema))
            {
                brands.Add(SchemaClassifier.MapBrand(name, schema));
                continue;
            }

            var schemaDescription = string.IsNullOrEmpty(schema.Description) ? null : schema.Description;

            if (schema.AllOf is { Count: > 0 })
            {
                var record = _synth.ResolveAllOfRecord(name, schema.AllOf, inheritedRequired: schema.Required);
                record = _synth.MergeWithSiblingProperties(record, schema, name);

                // Skip empty allOf records — resolved inline via $ref
                if (record.Properties.Count == 0 && schema.Properties is not { Count: > 0 })
                {
                    continue;
                }

                if (schemaDescription is not null)
                    record = record with { Description = schemaDescription };
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

                var unionRecord = _synth.ResolveUnionRecord(name, schema.OneOf);
                if (schemaDescription is not null)
                    unionRecord = unionRecord with { Description = schemaDescription };
                records.Add(unionRecord);
                continue;
            }

            if (schema.AnyOf is { Count: > 1 })
            {
                var anyOfRecord = _synth.ResolveUnionRecord(name, schema.AnyOf);
                if (schemaDescription is not null)
                    anyOfRecord = anyOfRecord with { Description = schemaDescription };
                records.Add(anyOfRecord);
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

                // Named diagnostic (I5, named-schema side): a record is generated from
                // `properties`, so an `additionalProperties` declared alongside is dropped.
                if (schema.AdditionalProperties is not null)
                {
                    _ctx.Warnings.Add(Diagnostics.Prefix(
                        Diagnostics.ImportAdditionalPropertiesDropped,
                        $"additionalProperties dropped on '{name}': schema has both 'properties' and 'additionalProperties' — imported as a record; extra members are not represented."));
                }

                // Named diagnostic (I.A-17): a discriminator on a plain object schema (no oneOf
                // union to dispatch over) has no C# contract representation — the record is
                // generated but the polymorphic dispatch semantics are dropped.
                if (schema.Discriminator?.PropertyName is { } discriminatorProperty)
                {
                    _ctx.Warnings.Add(Diagnostics.Prefix(
                        Diagnostics.ImportDiscriminatorDropped,
                        $"Discriminator dropped on '{name}': property '{discriminatorProperty}' has no oneOf union — imported as a regular record."));
                }

                records.Add(_synth.MapRecord(name, schema));
                continue;
            }

            // Primitive aliases (e.g. { "type": "string", "format": "date-time" }) — skip, resolved inline
        }

        // Register component records for shape-checked reuse (I3 residual)
        foreach (var record in records)
        {
            _ctx.MappedComponentRecords[record.Name] = record;
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

    /// <summary>
    /// I1: walks every component alias entry's $ref chain using raw reference ids
    /// (never proxied members) and records the final non-reference target, or marks
    /// the alias unresolvable (cycle / missing target) with a loud warning.
    /// </summary>
    private void ResolveAliasTargets(IDictionary<string, IOpenApiSchema> schemas)
    {
        foreach (var (key, schema) in schemas)
        {
            if (schema is not OpenApiSchemaReference)
            {
                continue;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { key };
            var current = key;

            while (true)
            {
                if (schemas[current] is not OpenApiSchemaReference reference)
                {
                    _aliasTargets[key] = current;
                    break;
                }

                var targetId = reference.Reference.Id;
                if (targetId is null || !schemas.ContainsKey(targetId))
                {
                    _ctx.Warnings.Add(Diagnostics.Prefix(
                        Diagnostics.ImportAliasTargetMissing,
                        $"Alias schema '{key}' references missing schema '{targetId ?? "(null)"}' — consumers fall back to JsonElement."));
                    _unresolvableAliases.Add(key);
                    break;
                }

                if (!visited.Add(targetId))
                {
                    _ctx.Warnings.Add(Diagnostics.Prefix(
                        Diagnostics.ImportAliasRefCycle,
                        $"Alias schema '{key}' is part of a $ref cycle ({string.Join(" -> ", visited)}) — consumers fall back to JsonElement."));
                    _unresolvableAliases.Add(key);
                    break;
                }

                current = targetId;
            }
        }
    }

    private bool TryResolveSchemaReference(OpenApiSchemaReference schemaRef, string? context, out string result)
    {
        result = "";

        var refId = schemaRef.Reference.Id;

        // I1: refs to unresolvable aliases (cycle/missing target) — loud fallback,
        // and never touch the proxy (a cyclic chain overflows the stack)
        if (refId is not null && _unresolvableAliases.Contains(refId))
        {
            _ctx.Warnings.Add(Diagnostics.Prefix(
                Diagnostics.ImportUnresolvableAliasReference,
                $"Reference to unresolvable alias schema '{refId}'{(context is null ? "" : $" (in '{context}')")} — using JsonElement."));
            result = "System.Text.Json.JsonElement";
            return true;
        }

        // I1: refs to alias entries resolve against the FINAL target schema and name
        var effective = (IOpenApiSchema)schemaRef;
        var effectiveId = refId;
        if (refId is not null
            && _aliasTargets.TryGetValue(refId, out var finalKey)
            && _componentSchemas is not null
            && _componentSchemas.TryGetValue(finalKey, out var finalSchema))
        {
            effective = finalSchema;
            effectiveId = finalKey;
        }

        // If the target is a property-less object schema, resolve to Dictionary
        // (no record was generated for it in MapSchemas) — unless marked as empty record
        if (SchemaClassifier.IsObject(effective) && effective.Properties is not { Count: > 0 }
            && !SchemaClassifier.HasExtension(effective, "x-rivet-empty-record"))
        {
            result = ResolveObjectType(effective, context);
            return true;
        }

        // If the target has x-rivet-generic, resolve to generic type string
        if (SchemaClassifier.TryGetGenericExtension(effective, out var genericInfo))
        {
            result = SchemaClassifier.BuildGenericTypeString(genericInfo!);
            return true;
        }

        // If the target would generate a type (record, enum, brand), use the mapped name
        // (alias-chased, dedup-aware). Otherwise it's a primitive alias — fall through to
        // resolve the underlying type.
        if (SchemaClassifier.WouldGenerateType(effective))
        {
            result = effectiveId is not null && _ctx.SchemaNameMap.TryGetValue(effectiveId, out var mapped)
                ? mapped
                : SanitizeName(effectiveId ?? schemaRef.Reference.Id!);
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
                var record = _synth.ResolveAllOfRecord(allOfName, schema.AllOf, inheritedRequired: schema.Required);
                record = _synth.MergeWithSiblingProperties(record, schema, allOfName);
                result = _ctx.AddOrReuseExtraRecord(record) + "?";
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

        // oneOf/anyOf with an explicit null branch (nullable ref/composite in 3.1)
        if (TryResolveTwoBranchNullable(schema.OneOf, context, out result)
            || TryResolveTwoBranchNullable(schema.AnyOf, context, out result))
        {
            return true;
        }

        // anyOf with single element (OpenAPI 3.0 nullable pattern)
        if (schema.AnyOf is { Count: 1 })
        {
            result = ResolveCSharpType(schema.AnyOf[0], context);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the 3.1 nullable composition: exactly two branches, one of which is the
    /// explicit <c>{"type": "null"}</c> schema. The emitter uses oneOf for $ref inners
    /// and anyOf for typeless composites (where an untyped branch would also match null
    /// and make oneOf ambiguous).
    /// </summary>
    private bool TryResolveTwoBranchNullable(IList<IOpenApiSchema>? branches, string? context, out string result)
    {
        result = "";

        if (branches is not { Count: 2 })
        {
            return false;
        }

        IOpenApiSchema? valuePart = null;
        var hasNull = false;

        foreach (var item in branches)
        {
            if (item.Type.HasValue && item.Type.Value == JsonSchemaType.Null)
            {
                hasNull = true;
            }
            else
            {
                valuePart = item;
            }
        }

        if (!hasNull || valuePart is null)
        {
            return false;
        }

        var resolved = ResolveCSharpType(valuePart, context);
        result = resolved.EndsWith("?") ? resolved : resolved + "?";
        return true;
    }

    private bool TryResolveComposition(IOpenApiSchema schema, string? context, out string result)
    {
        result = "";

        // allOf inline → synthetic flattened record
        if (schema.AllOf is { Count: > 0 })
        {
            // Short-circuit: allOf with a single $ref and no sibling properties → resolve the ref
            // recursively (I2): refs to primitives/enums resolve to the underlying type instead
            // of a dangling name for a record that was never emitted.
            if (schema.AllOf.Count == 1
                && schema.AllOf[0] is OpenApiSchemaReference
                && schema.Properties is not { Count: > 0 })
            {
                result = ResolveCSharpType(schema.AllOf[0], context);
                return true;
            }

            var allOfName = context ?? _ctx.NextSyntheticName("Composed");
            var record = _synth.ResolveAllOfRecord(allOfName, schema.AllOf, inheritedRequired: schema.Required);
            record = _synth.MergeWithSiblingProperties(record, schema, allOfName);
            result = _ctx.AddOrReuseExtraRecord(record);
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
        return _ctx.AddOrReuseExtraRecord(record);
    }

    private string ResolveFallbackType(IOpenApiSchema schema, string? context)
    {
        // enum without explicit type (common in some generators)
        if (schema.Enum is { Count: > 1 })
        {
            if (SchemaClassifier.IsIntEnum(schema))
                return SynthesizeInlineIntEnum(schema, context);
            return SynthesizeInlineEnum(schema, context);
        }

        if (schema.Enum is { Count: > 0 })
        {
            WarnEnumConstraintDropped(schema, context, "string");
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
            return WarnAndFallback(Diagnostics.ImportUnresolvedSchema, "Schema could not be resolved to a C# type");
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

            var stringType = SchemaClassifier.ResolveStringType(schema);
            if (schema.Enum is { Count: > 0 })
            {
                WarnEnumConstraintDropped(schema, context, stringType);
            }

            return stringType;
        }

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            if (schema.Enum is { Count: > 1 } && SchemaClassifier.IsIntEnum(schema))
            {
                return SynthesizeInlineIntEnum(schema, context);
            }

            var integerType = SchemaClassifier.ResolveIntegerType(schema);
            if (schema.Enum is { Count: > 0 })
            {
                WarnEnumConstraintDropped(schema, context, integerType);
            }

            return integerType;
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

        return WarnAndFallback(Diagnostics.ImportUnsupportedSchemaType, $"Unsupported JSON Schema type '{type}'");
    }

    private string SanitizeName(string name)
    {
        if (_ctx.SchemaNameMap.TryGetValue(name, out var mapped))
        {
            return mapped;
        }

        return Naming.ToPascalCaseFromSegments(name);
    }

    private string WarnAndFallback(string diagnosticId, string reason)
    {
        _ctx.Warnings.Add(Diagnostics.Prefix(diagnosticId, $"{reason} — mapped to 'JsonElement'."));
        return "System.Text.Json.JsonElement";
    }

    /// <summary>
    /// Named diagnostic (I.A-15): an enum constraint that cannot be represented as a C# enum
    /// (single value, mixed/float values, out-of-int32-range values) degrades to a primitive.
    /// Never silent — the values are dropped from the generated contract.
    /// </summary>
    private void WarnEnumConstraintDropped(IOpenApiSchema schema, string? context, string degradedTo)
    {
        var values = string.Join(", ", schema.Enum!.Select(v => v?.ToJsonString() ?? "null"));
        var where = context is not null ? $" at '{context}'" : "";
        _ctx.Warnings.Add(Diagnostics.Prefix(
            Diagnostics.ImportEnumConstraintDropped,
            $"Enum constraint dropped{where}: values [{values}] degraded to '{degradedTo}'."));
    }

    private string ResolveArrayType(IOpenApiSchema schema, string? context)
    {
        if (schema.Items is not null)
        {
            var itemType = ResolveCSharpType(schema.Items, context);
            return $"List<{itemType}>";
        }

        return $"List<{WarnAndFallback(Diagnostics.ImportArrayMissingItems, "Array schema missing 'items'")}>";
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
        var finalName = _ctx.AddOrReuseExtraEnum(enumDef);
        _ctx.SchemaFingerprints[fingerprint] = finalName;
        return finalName;
    }

    private string SynthesizeInlineIntEnum(IOpenApiSchema schema, string? context)
    {
        var fingerprint = SchemaClassifier.ComputeSchemaFingerprint(schema);
        if (_ctx.SchemaFingerprints.TryGetValue(fingerprint, out var existingName))
        {
            return existingName;
        }

        var name = context ?? _ctx.NextSyntheticName("Enum");
        var enumDef = SchemaClassifier.MapIntEnum(name, schema);
        var finalName = _ctx.AddOrReuseExtraEnum(enumDef);
        _ctx.SchemaFingerprints[fingerprint] = finalName;
        return finalName;
    }

    private string ResolveObjectType(IOpenApiSchema schema, string? context)
    {
        if (schema.AdditionalProperties is not null)
        {
            // Named diagnostic (I5): an inline object declaring BOTH `properties` and
            // `additionalProperties` maps to a dictionary — the declared properties are
            // dropped, never silently.
            if (schema.Properties is { Count: > 0 })
            {
                var dropped = string.Join(", ", schema.Properties.Keys);
                var where = context is not null ? $" at '{context}'" : "";
                _ctx.Warnings.Add(Diagnostics.Prefix(
                    Diagnostics.ImportDeclaredPropertiesDropped,
                    $"Declared properties dropped{where}: schema has both 'properties' and 'additionalProperties' — imported as a dictionary; properties [{dropped}] are not represented."));
            }

            var valueType = ResolveCSharpType(schema.AdditionalProperties, context);
            var keyType = ResolveDictionaryKeyType(schema, context);
            return $"Dictionary<{keyType}, {valueType}>";
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
            var finalName = _ctx.AddOrReuseExtraRecord(record);
            _ctx.SchemaFingerprints[fingerprint] = finalName;
            return finalName;
        }

        // Bare object with no properties or additionalProperties → untyped map
        return "Dictionary<string, System.Text.Json.JsonElement>";
    }

    /// <summary>
    /// Resolves a dictionary schema's key type from its <c>propertyNames</c> schema
    /// (Microsoft.OpenApi surfaces the keyword via UnrecognizedKeywords). Mirrors the
    /// emitter's key support: a $ref to a string enum or string-backed brand component,
    /// or an inline string schema whose format / x-rivet-csharp-type pins the C# key
    /// type. Unsupported shapes degrade to string keys with a named warning (RIV3014).
    /// </summary>
    private string ResolveDictionaryKeyType(IOpenApiSchema schema, string? context)
    {
        if (schema.UnrecognizedKeywords is null
            || !schema.UnrecognizedKeywords.TryGetValue("propertyNames", out var node)
            || node is null)
        {
            return "string";
        }

        if (node is JsonObject obj)
        {
            // $ref → enum or string-backed brand component
            if (GetStringMember(obj, "$ref") is { } refValue)
            {
                const string prefix = "#/components/schemas/";
                if (refValue.StartsWith(prefix, StringComparison.Ordinal)
                    && TryResolveComponentKeyType(refValue[prefix.Length..], out var keyName))
                {
                    return keyName;
                }
            }
            else
            {
                // x-rivet-csharp-type pins the exact key type (numeric-keyed dictionaries etc.)
                if (GetStringMember(obj, "x-rivet-csharp-type") is { } csharpType)
                {
                    return csharpType;
                }

                if (GetStringMember(obj, "type") == "string")
                {
                    return GetStringMember(obj, "format") switch
                    {
                        "date-time" => "DateTime",
                        "date" => "DateOnly",
                        "time" => "TimeOnly",
                        "guid" or "uuid" => "Guid",
                        "uri" => "Uri",
                        "int32" => "int",
                        "int64" => "long",
                        "int16" => "short",
                        "uint16" => "ushort",
                        "uint8" => "byte",
                        "int8" => "sbyte",
                        "uint32" => "uint",
                        "uint64" => "ulong",
                        "float" => "float",
                        "double" => "double",
                        "decimal" => "decimal",
                        // No/unknown format on string keys — still plain string keys
                        _ => "string",
                    };
                }
            }
        }

        var where = context is not null ? $" at '{context}'" : "";
        _ctx.Warnings.Add(Diagnostics.Prefix(
            Diagnostics.ImportDictionaryKeyDropped,
            $"propertyNames key schema{where} has no C# dictionary-key representation — imported with string keys."));
        return "string";
    }

    private static string? GetStringMember(JsonObject obj, string name)
        => obj.TryGetPropertyValue(name, out var node) && node is JsonValue value
            && value.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>
    /// True when the referenced component (alias-chased) is representable as a C#
    /// dictionary key: a string enum or a string-backed brand. Anything else (records,
    /// int enums, non-string brands) is not a valid key target — the caller degrades.
    /// </summary>
    private bool TryResolveComponentKeyType(string refId, out string keyName)
    {
        keyName = "";

        var finalId = _aliasTargets.TryGetValue(refId, out var target) ? target : refId;
        if (_componentSchemas is null
            || !_componentSchemas.TryGetValue(finalId, out var componentSchema)
            || !_ctx.SchemaNameMap.TryGetValue(finalId, out var mapped))
        {
            return false;
        }

        if (SchemaClassifier.IsStringEnum(componentSchema)
            || (SchemaClassifier.IsBrand(componentSchema)
                && componentSchema.Type?.HasFlag(JsonSchemaType.String) == true))
        {
            keyName = mapped;
            return true;
        }

        return false;
    }
}
