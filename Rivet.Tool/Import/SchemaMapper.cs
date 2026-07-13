using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Rivet.Tool.Model;

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
    private readonly HashSet<string> _skippedComponentTypes = new(StringComparer.Ordinal);
    private IDictionary<string, IOpenApiSchema>? _componentSchemas;
    private readonly HashSet<string> _unsupportedNamedScalarsWarned = new(StringComparer.Ordinal);
    private readonly HashSet<string> _requiredComponentSchemas = new(StringComparer.Ordinal);

    // P2 wave 4: oneOf + discriminator + usable mapping reverses to an abstract
    // [JsonPolymorphic] base record with [JsonDerivedType] registrations. Bases are
    // keyed by schema key; each conforming variant key maps back to its base key;
    // bases whose mapping could NOT be reversed record the reason for the loud
    // RIV3005 fallback warning.
    private readonly Dictionary<string, PolymorphicUnion> _polymorphicBases = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, string> _polymorphicVariantKeys = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, string> _polymorphicRejections = new(
        StringComparer.Ordinal
    );

    private sealed record PolymorphicUnion(
        string DiscriminatorProperty,
        IReadOnlyList<(string Tag, string VariantKey)> Variants
    );

    public SchemaMapper(List<string> warnings)
    {
        _ctx = new ResolutionContext(warnings);
        _synth = new RecordSynthesizer(
            _ctx,
            ResolveCSharpType,
            ResolveFormat,
            ResolveSchemaType,
            ResolveScalarReferenceName
        );
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
    /// P2 wave 5: the header-augmented replacement for a component record, or null when
    /// the record was not augmented. Consulted by OpenApiImporter when writing Types/.
    /// </summary>
    public GeneratedRecord? GetComponentRecordOverride(string name) =>
        _ctx.ComponentRecordOverrides.TryGetValue(name, out var record) ? record : null;

    /// <summary>
    /// True when a components/schemas RECORD with this name exists AND its shape
    /// (property names, C# types, required-ness) matches the candidate properties.
    /// Name-only matches return false — callers must disambiguate instead of reusing.
    /// </summary>
    public bool HasMappedSchemaWithShape(string name, IReadOnlyList<RecordProperty> properties)
    {
        if (
            !_ctx.MappedComponentRecords.TryGetValue(name, out var record)
            || record.Properties.Count != properties.Count
        )
        {
            return false;
        }

        var byName = record.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var prop in properties)
        {
            if (
                !byName.TryGetValue(prop.Name, out var existing)
                || existing.CSharpType != prop.CSharpType
                || existing.IsRequired != prop.IsRequired
                // P2 wave 5: a header-bound property is a different shape from a plain
                // one of the same name/type — headers never enter the JSON schema.
                || existing.HeaderName != prop.HeaderName
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// P2 wave 5: header-aware component reuse for synthesized inputs. [RivetHeader]
    /// properties never enter a JSON schema, so the component a previous emit∘import loop
    /// produced for a header-bearing input carries only the NON-header subset. When that
    /// subset matches (base name first, then numbered variants), the component record is
    /// REPLACED by an augmented copy carrying the header properties (plain properties keep
    /// the component's instances — descriptions/formats survive) and its name is returned.
    /// Null when nothing matches or the candidate carries no header properties.
    /// </summary>
    public string? AugmentComponentWithHeaderShape(
        string baseName,
        IReadOnlyList<RecordProperty> properties
    )
    {
        if (!properties.Any(p => p.HeaderName is not null))
        {
            return null;
        }

        var plain = properties.Where(p => p.HeaderName is null).ToList();

        foreach (var candidate in ComponentCandidates(baseName))
        {
            if (
                !_ctx.MappedComponentRecords.TryGetValue(candidate, out var record)
                // An already-augmented record (headers attached for another endpoint) is
                // never re-clobbered here — exact matches were handled by the callers.
                || record.Properties.Any(p => p.HeaderName is not null)
                || !HasMappedSchemaWithShape(candidate, plain)
            )
            {
                continue;
            }

            var byName = record.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var augmented = properties
                .Select(p => p.HeaderName is null ? byName[p.Name] : p)
                .ToList();

            _ctx.ReplaceComponentRecord(candidate, record with { Properties = augmented });
            return candidate;
        }

        return null;
    }

    /// <summary>
    /// P2 wave 5 (body-merge case): replaces a component record's properties with a merged
    /// list that differs ONLY by [RivetHeader] properties — the JSON shape (non-header
    /// subset) must be unchanged, or the call refuses and returns false. Idempotent: an
    /// identical property list returns true without touching anything.
    /// </summary>
    public bool TryAugmentComponentRecord(string name, IReadOnlyList<RecordProperty> merged)
    {
        if (!_ctx.MappedComponentRecords.TryGetValue(name, out var record))
        {
            return false;
        }

        if (record.Properties.SequenceEqual(merged))
        {
            return true;
        }

        var existingPlain = record.Properties.Where(p => p.HeaderName is null).ToList();
        var mergedPlain = merged.Where(p => p.HeaderName is null).ToList();
        if (!existingPlain.SequenceEqual(mergedPlain))
        {
            return false;
        }

        _ctx.ReplaceComponentRecord(name, record with { Properties = merged.ToList() });
        return true;
    }

    /// <summary>Base name first, then numbered variants ordered by suffix.</summary>
    private IEnumerable<string> ComponentCandidates(string baseName)
    {
        yield return baseName;

        foreach (
            var name in _ctx
                .MappedComponentRecords.Keys.Select(name =>
                    (Name: name, Suffix: ParseNumberedSuffix(name, baseName))
                )
                .Where(entry => entry.Suffix is not null)
                .OrderBy(entry => entry.Suffix!.Value)
                .Select(entry => entry.Name)
        )
        {
            yield return name;
        }
    }

    /// <summary>
    /// Finds an existing numbered components/schemas variant (<c>{baseName}2</c>,
    /// <c>{baseName}3</c>, …) whose shape matches exactly, lowest suffix first.
    /// A prior emit∘import loop may already have disambiguated a synthesized input to a
    /// numbered name — reusing it (instead of minting a fresh suffix every loop) keeps
    /// emit∘import a fixed point (GAP-2, I3 residual). Null when nothing matches.
    /// </summary>
    public string? FindNumberedSchemaWithShape(
        string baseName,
        IReadOnlyList<RecordProperty> properties
    )
    {
        return _ctx
            .MappedComponentRecords.Keys.Select(name =>
                (Name: name, Suffix: ParseNumberedSuffix(name, baseName))
            )
            .Where(entry => entry.Suffix is not null)
            .OrderBy(entry => entry.Suffix!.Value)
            .Where(entry => HasMappedSchemaWithShape(entry.Name, properties))
            .Select(entry => entry.Name)
            .FirstOrDefault();
    }

    private static int? ParseNumberedSuffix(string name, string baseName)
    {
        if (name.Length <= baseName.Length || !name.StartsWith(baseName, StringComparison.Ordinal))
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
    public SchemaMapResult MapSchemas(
        IDictionary<string, IOpenApiSchema> schemas,
        IReadOnlySet<string>? requiredComponentSchemas = null
    )
    {
        var records = new List<GeneratedRecord>();
        var enums = new List<GeneratedEnum>();
        var brands = new List<GeneratedBrand>();
        var scalarSchemas = new List<GeneratedScalarSchema>();
        // Case-INSENSITIVE: emitted type names become Types/{Name}.cs files, and on
        // APFS/NTFS two names differing only by case clobber each other at write time
        // (cloudflare: ...CustomHostname vs ...Customhostname left a dangling type).
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // I1: resolve alias chains first, using raw reference ids only (never proxied
        // members — a cyclic alias would overflow the stack inside the library's proxy)
        _componentSchemas = schemas;
        if (requiredComponentSchemas is not null)
        {
            _requiredComponentSchemas.UnionWith(requiredComponentSchemas);
        }
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

            if (TryGetScalarAliasTarget(key, schemas, out _, out _))
            {
                var name = SanitizeName(key);
                if (!usedNames.Add(name))
                {
                    var suffix = 2;
                    while (!usedNames.Add($"{name}_{suffix}"))
                    {
                        suffix++;
                    }
                    name = $"{name}_{suffix}";
                }
                _ctx.SchemaNameMap[key] = name;
                _ctx.ReservedTypeNames.Add(name);
                continue;
            }

            if (
                _aliasTargets.TryGetValue(key, out var finalKey)
                && _ctx.SchemaNameMap.TryGetValue(finalKey, out var targetName)
            )
            {
                _ctx.SchemaNameMap[key] = targetName;
            }
        }

        // P2 wave 4: detect reversible polymorphic unions BEFORE mapping so variant
        // schemas can be generated as derived records regardless of iteration order.
        DetectPolymorphicUnions(schemas);

        // A single-ref allOf around a union has no record property surface. Mark these
        // before mapping so earlier consumers resolve through the wrapper instead of
        // naming a C# record that MapSchemas will intentionally skip.
        foreach (var (key, schema) in schemas)
        {
            if (
                schema is not OpenApiSchemaReference
                && schema.AllOf is [OpenApiSchemaReference { Reference.Id: { } onlyId }]
                && schema.Properties is not { Count: > 0 }
                && schemas.TryGetValue(DecodeComponentId(onlyId)!, out var onlyTarget)
                && (onlyTarget.OneOf is { Count: > 0 } || onlyTarget.AnyOf is { Count: > 1 })
            )
            {
                _skippedComponentTypes.Add(key);
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

            if (TryMapNamedArray(name, key, schema, out var arraySchema))
            {
                scalarSchemas.Add(arraySchema);
                continue;
            }

            if (TryMapNamedScalar(name, key, schema, out var scalarSchema))
            {
                scalarSchemas.Add(scalarSchema);
            }
            else if (TryMapNamedUntyped(name, key, schema, out var untypedSchema))
            {
                scalarSchemas.Add(untypedSchema);
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

            var schemaDescription = string.IsNullOrEmpty(schema.Description)
                ? null
                : schema.Description;

            // P2 wave 4: a schema claimed as a polymorphic union variant becomes a
            // derived record: the discriminator property is STRIPPED (System.Text.Json
            // re-adds it on the wire — keeping it would double-emit) and the record
            // inherits from the abstract base.
            if (_polymorphicVariantKeys.TryGetValue(key, out var polymorphicBaseKey))
            {
                var derived = _synth.MapRecord(name, schema);
                var discName = Naming.ToPascalCaseFromSegments(
                    _polymorphicBases[polymorphicBaseKey].DiscriminatorProperty
                );
                if (discName == name)
                {
                    discName += "Value";
                }

                derived = derived with
                {
                    Properties = derived.Properties.Where(p => p.Name != discName).ToList(),
                    BaseTypeName = _ctx.SchemaNameMap[polymorphicBaseKey],
                    Description = schemaDescription,
                };
                records.Add(derived);
                continue;
            }

            if (schema.AllOf is { Count: > 0 })
            {
                var record = _synth.ResolveAllOfRecord(
                    name,
                    schema.AllOf,
                    inheritedRequired: schema.Required
                );
                record = _synth.MergeWithSiblingProperties(record, schema, name);
                record = record with { SchemaMetadata = CollectGeneratedSchemaMetadata(schema) };

                // Skip empty allOf records — resolved inline via $ref
                if (record.Properties.Count == 0 && schema.Properties is not { Count: > 0 })
                {
                    _skippedComponentTypes.Add(key);
                    continue;
                }

                if (schemaDescription is not null)
                {
                    record = record with { Description = schemaDescription };
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

                // P2 wave 4: oneOf + discriminator + usable mapping reverses to an
                // abstract [JsonPolymorphic] base record with one [JsonDerivedType]
                // registration per mapping entry.
                if (_polymorphicBases.TryGetValue(key, out var poly))
                {
                    var variantRefs = poly
                        .Variants.Select(v => new PolymorphicVariantRef(
                            _ctx.SchemaNameMap[v.VariantKey],
                            v.Tag
                        ))
                        .ToList();
                    records.Add(
                        new GeneratedRecord(
                            name,
                            [],
                            Description: schemaDescription,
                            Polymorphism: new PolymorphismInfo(
                                poly.DiscriminatorProperty,
                                variantRefs
                            )
                        )
                    );
                    continue;
                }

                // A oneOf carrying a discriminator the importer could NOT reverse —
                // never silently: the drop names the reason (RIV3005).
                if (schema.Discriminator?.PropertyName is { } droppedDiscriminator)
                {
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportDiscriminatorDropped,
                            $"Discriminator dropped on '{name}': property '{droppedDiscriminator}' has no usable oneOf mapping "
                                + $"({_polymorphicRejections.GetValueOrDefault(key, "mapping absent")}) — imported as a union wrapper record."
                        )
                    );
                }

                var unionRecord = _synth.ResolveUnionRecord(name, schema.OneOf);
                if (schemaDescription is not null)
                {
                    unionRecord = unionRecord with { Description = schemaDescription };
                }

                records.Add(unionRecord);
                continue;
            }

            if (schema.AnyOf is { Count: > 1 })
            {
                var anyOfRecord = _synth.ResolveUnionRecord(name, schema.AnyOf);
                if (schemaDescription is not null)
                {
                    anyOfRecord = anyOfRecord with { Description = schemaDescription };
                }

                records.Add(anyOfRecord);
                continue;
            }

            if (SchemaClassifier.IsObject(schema))
            {
                // A named empty object is still a component identity. Generate an empty
                // record so import→emit does not erase the component key; anonymous empty
                // objects continue to resolve as dictionaries in ResolveObjectType.
                if (schema.Properties is not { Count: > 0 })
                {
                    if (schema.AdditionalProperties is null)
                    {
                        records.Add(
                            new GeneratedRecord(
                                name,
                                [],
                                Description: schemaDescription,
                                SchemaMetadata: CollectGeneratedSchemaMetadata(schema),
                                HasExtensionData: schema.AdditionalPropertiesAllowed
                            )
                        );
                    }
                    continue;
                }

                // Named diagnostic (I.A-17): a discriminator on a plain object schema (no oneOf
                // union to dispatch over) has no C# contract representation — the record is
                // generated but the polymorphic dispatch semantics are dropped.
                if (schema.Discriminator?.PropertyName is { } discriminatorProperty)
                {
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportDiscriminatorDropped,
                            $"Discriminator dropped on '{name}': property '{discriminatorProperty}' has no oneOf union — imported as a regular record."
                        )
                    );
                }

                records.Add(_synth.MapRecord(name, schema));
                continue;
            }

            // Primitive aliases (e.g. { "type": "string", "format": "date-time" }) — skip, resolved inline
        }

        foreach (var (key, schema) in schemas)
        {
            if (
                schema is not OpenApiSchemaReference { Reference.Id: { } directId }
                || !TryGetScalarAliasTarget(key, schemas, out var targetKey, out var target)
                || !_ctx.SchemaNameMap.TryGetValue(key, out var aliasName)
                || !_ctx.SchemaNameMap.TryGetValue(DecodeComponentId(directId)!, out var directName)
                || !TryMapNamedScalar("_", targetKey, target, out var targetScalar)
            )
            {
                continue;
            }

            scalarSchemas.Add(
                new GeneratedScalarSchema(
                    aliasName,
                    key,
                    targetScalar.SchemaType,
                    null,
                    false,
                    new TsScalarMetadata(),
                    SchemaRef: directName
                )
            );
        }

        // Register component records for shape-checked reuse (I3 residual)
        var componentIdsByName = _ctx
            .SchemaNameMap.Where(pair => schemas[pair.Key] is not OpenApiSchemaReference)
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

        for (var i = 0; i < records.Count; i++)
        {
            if (componentIdsByName.TryGetValue(records[i].Name, out var componentId))
            {
                records[i] = records[i] with { ComponentId = componentId, IsSynthetic = false };
            }
        }
        for (var i = 0; i < enums.Count; i++)
        {
            if (componentIdsByName.TryGetValue(enums[i].Name, out var componentId))
            {
                enums[i] = enums[i] with { ComponentId = componentId, IsSynthetic = false };
            }
        }
        for (var i = 0; i < brands.Count; i++)
        {
            if (componentIdsByName.TryGetValue(brands[i].Name, out var componentId))
            {
                brands[i] = brands[i] with { ComponentId = componentId, IsSynthetic = false };
            }
        }

        var representedComponentIds = records
            .Select(record => record.ComponentId)
            .Concat(enums.Select(value => value.ComponentId))
            .Concat(brands.Select(value => value.ComponentId))
            .Concat(scalarSchemas.Select(value => value.ComponentId))
            .Where(componentId => componentId is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var componentId in _requiredComponentSchemas.Order(StringComparer.Ordinal))
        {
            if (
                representedComponentIds.Contains(componentId)
                || !schemas.TryGetValue(componentId, out var schema)
                || schema is OpenApiSchemaReference
                || !_ctx.SchemaNameMap.TryGetValue(componentId, out var name)
                || (schema.Type & ~JsonSchemaType.Null) == JsonSchemaType.Array
                    && schema.Items is not null
            )
            {
                continue;
            }

            _ctx.Warnings.Add(
                Diagnostics.Prefix(
                    Diagnostics.ImportUnsupportedSchemaType,
                    $"Named schema component '{componentId}' is required by preserved component identity but has no reversible C# representation — preserving its identity as an untyped fallback component."
                )
            );
            scalarSchemas.Add(
                new GeneratedScalarSchema(
                    name,
                    componentId,
                    SchemaType: null,
                    Format: null,
                    IsNullable: false,
                    Metadata: BuildScalarMetadata(schema, isNullable: false)
                )
            );
        }

        foreach (var record in records)
        {
            _ctx.MappedComponentRecords[record.Name] = record;
        }

        return new SchemaMapResult(records, enums, brands, scalarSchemas);
    }

    private bool TryMapNamedScalar(
        string name,
        string componentId,
        IOpenApiSchema schema,
        out GeneratedScalarSchema scalar
    )
    {
        scalar = null!;
        if (schema.Type is not { } declared)
        {
            return false;
        }

        var nonNull = declared & ~JsonSchemaType.Null;
        var schemaType = nonNull switch
        {
            JsonSchemaType.String => "string",
            JsonSchemaType.Integer => "integer",
            JsonSchemaType.Number => "number",
            JsonSchemaType.Boolean => "boolean",
            _ => null,
        };
        if (schemaType is null)
        {
            if (
                nonNull.HasFlag(JsonSchemaType.String)
                || nonNull.HasFlag(JsonSchemaType.Integer)
                || nonNull.HasFlag(JsonSchemaType.Number)
                || nonNull.HasFlag(JsonSchemaType.Boolean)
            )
            {
                WarnUnsupportedNamedScalar(
                    componentId,
                    "heterogeneous leaf types; only one primitive leaf plus optional null is supported"
                );
            }
            return false;
        }

        if (
            schema.AllOf is { Count: > 0 }
            || schema.OneOf is { Count: > 0 }
            || schema.AnyOf is { Count: > 0 }
            || schema.Const is not null
        )
        {
            WarnUnsupportedNamedScalar(
                componentId,
                "const or schema composition; only primitive leaves and flat string/Int32 enums are supported"
            );
            return false;
        }

        if (
            schema.Properties is { Count: > 0 }
            || schema.Items is not null
            || schema.AdditionalProperties is not null
        )
        {
            return false;
        }

        if (
            schema.Enum is { Count: > 0 }
            && !SchemaClassifier.IsStringEnum(schema)
            && !SchemaClassifier.IsIntEnum(schema)
        )
        {
            WarnUnsupportedNamedScalar(
                componentId,
                "a heterogeneous or unsupported enum; only flat string and Int32 integer enums are supported"
            );
            return false;
        }

        var metadata = BuildScalarMetadata(schema, declared.HasFlag(JsonSchemaType.Null));
        scalar = new GeneratedScalarSchema(
            name,
            componentId,
            schemaType,
            schema.Format,
            declared.HasFlag(JsonSchemaType.Null),
            metadata,
            IsEnum: schema.Enum is { Count: > 0 }
        );
        return true;
    }

    private bool TryMapNamedArray(
        string name,
        string componentId,
        IOpenApiSchema schema,
        out GeneratedScalarSchema array
    )
    {
        array = null!;
        if (
            (schema.Type & ~JsonSchemaType.Null) != JsonSchemaType.Array
            || schema.Items is not OpenApiSchemaReference { Reference.Id: { } itemId }
            || schema.AllOf is { Count: > 0 }
            || schema.OneOf is { Count: > 0 }
            || schema.AnyOf is { Count: > 0 }
            || schema.Const is not null
            || schema.Properties is { Count: > 0 }
            || schema.AdditionalProperties is not null
            || schema.Enum is { Count: > 0 }
        )
        {
            return false;
        }

        var itemComponentId = DecodeComponentId(itemId);
        if (
            itemComponentId is null
            || !_ctx.SchemaNameMap.TryGetValue(itemComponentId, out var itemSchemaRef)
        )
        {
            return false;
        }
        _requiredComponentSchemas.Add(itemComponentId);

        array = new GeneratedScalarSchema(
            name,
            componentId,
            SchemaType: "array",
            Format: schema.Format,
            IsNullable: schema.Type?.HasFlag(JsonSchemaType.Null) == true,
            Metadata: BuildScalarMetadata(
                schema,
                schema.Type?.HasFlag(JsonSchemaType.Null) == true
            ),
            IsArray: true,
            ItemSchemaRef: itemSchemaRef
        );
        return true;
    }

    private static bool TryMapNamedUntyped(
        string name,
        string componentId,
        IOpenApiSchema schema,
        out GeneratedScalarSchema untyped
    )
    {
        untyped = null!;
        if (
            schema.Type.HasValue
            || schema.Properties is { Count: > 0 }
            || schema.Items is not null
            || schema.AdditionalProperties is not null
            || schema.AllOf is { Count: > 0 }
            || schema.OneOf is { Count: > 0 }
            || schema.AnyOf is { Count: > 0 }
            || schema.Const is not null
            || schema.Enum is { Count: > 0 }
        )
        {
            return false;
        }

        untyped = new GeneratedScalarSchema(
            name,
            componentId,
            SchemaType: null,
            Format: null,
            IsNullable: false,
            Metadata: BuildScalarMetadata(schema, isNullable: false)
        );
        return true;
    }

    internal static TsScalarMetadata BuildScalarMetadata(IOpenApiSchema schema, bool isNullable) =>
        new(
            Description: schema.Description,
            DefaultValue: schema.Default?.ToJsonString(),
            Example: schema.Example is null
                ? null
                : OpenApiJsonNodeSerializer.Serialize(schema.Example),
            Examples: schema.Examples is { Count: > 0 }
                ? new JsonArray(
                    schema
                        .Examples.Select(example =>
                            example is null ? null : OpenApiJsonNodeSerializer.Clone(example)
                        )
                        .ToArray()
                ).ToJsonString()
                : null,
            IsDeprecated: schema.Deprecated,
            IsReadOnly: schema.ReadOnly,
            IsWriteOnly: schema.WriteOnly,
            Constraints: RecordSynthesizer.ExtractConstraints(schema),
            IsNullable: isNullable || HasExplicitNullBranch(schema),
            Title: schema.Title,
            Xml: schema.Xml is null
                ? null
                : new TsSchemaXmlMetadata(
                    schema.Xml.Name,
                    schema.Xml.Namespace?.ToString(),
                    schema.Xml.Prefix,
                    schema.Xml.Attribute,
                    schema.Xml.Wrapped
                ),
            Format: schema.Format,
            IsFormatSpecified: schema.Format is not null
                || (schema.Type & ~JsonSchemaType.Null)
                    is JsonSchemaType.Integer
                        or JsonSchemaType.Number,
            Required: schema.Required is { Count: > 0 } ? schema.Required.ToList() : null
        );

    private static bool HasExplicitNullBranch(IOpenApiSchema schema) =>
        new[] { schema.OneOf, schema.AnyOf }.Any(branches =>
            branches is { Count: 2 } && branches.Any(branch => branch.Type == JsonSchemaType.Null)
        );

    internal static IReadOnlyList<GeneratedSchemaMetadata> CollectGeneratedSchemaMetadata(
        IOpenApiSchema schema
    )
    {
        if (schema is OpenApiSchemaReference)
        {
            return [];
        }
        var result = new List<GeneratedSchemaMetadata>();
        CollectGeneratedSchemaMetadata(schema, "", result, new HashSet<IOpenApiSchema>());
        return result;
    }

    private static void CollectGeneratedSchemaMetadata(
        IOpenApiSchema schema,
        string pointer,
        List<GeneratedSchemaMetadata> result,
        HashSet<IOpenApiSchema> visited
    )
    {
        if (!visited.Add(schema))
        {
            return;
        }

        var metadata = BuildScalarMetadata(
            schema,
            schema.Type?.HasFlag(JsonSchemaType.Null) == true
        );
        if (HasGeneratedSchemaMetadata(metadata))
        {
            result.Add(new GeneratedSchemaMetadata(pointer, metadata));
        }

        if (schema.Items is not null)
        {
            CollectGeneratedSchemaMetadata(schema.Items, pointer + "/items", result, visited);
        }
        if (schema.AdditionalProperties is not null)
        {
            CollectGeneratedSchemaMetadata(
                schema.AdditionalProperties,
                pointer + "/additionalProperties",
                result,
                visited
            );
        }
        visited.Remove(schema);
    }

    private static bool HasGeneratedSchemaMetadata(TsScalarMetadata metadata) =>
        metadata.Description is not null
        || metadata.Title is not null
        || metadata.DefaultValue is not null
        || metadata.Example is not null
        || metadata.Examples is not null
        || metadata.IsDeprecated
        || metadata.IsReadOnly
        || metadata.IsWriteOnly
        || metadata.IsNullable
        || metadata.IsFormatSpecified
        || metadata.Required is { Count: > 0 }
        || metadata.Constraints is { HasAny: true }
        || metadata.Xml is not null;

    private void WarnUnsupportedNamedScalar(string componentId, string reason)
    {
        if (!_unsupportedNamedScalarsWarned.Add(componentId))
        {
            return;
        }

        _ctx.Warnings.Add(
            Diagnostics.Prefix(
                Diagnostics.ImportNamedScalarAlgebraUnsupported,
                $"Named scalar component '{componentId}' uses {reason}. Existing fallback mapping retained."
            )
        );
    }

    internal string? ResolveScalarReferenceName(IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchemaReference { Reference.Id: { } rawId })
        {
            return null;
        }

        var componentId = DecodeComponentId(rawId);
        if (
            componentId is null
            || _componentSchemas is null
            || !_componentSchemas.TryGetValue(componentId, out var target)
            || (
                !TryGetScalarAliasTarget(componentId, _componentSchemas, out _, out _)
                && !TryMapNamedArray("_", componentId, target, out _)
            )
        )
        {
            return null;
        }

        return _ctx.SchemaNameMap.GetValueOrDefault(componentId);
    }

    private bool TryGetScalarAliasTarget(
        string componentId,
        IDictionary<string, IOpenApiSchema> schemas,
        out string targetKey,
        out IOpenApiSchema target
    )
    {
        targetKey = _aliasTargets.GetValueOrDefault(componentId, componentId);
        if (
            !schemas.TryGetValue(targetKey, out target!)
            || target is OpenApiSchemaReference
            || !TryMapNamedScalar("_", targetKey, target, out _)
        )
        {
            target = null!;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolve an OpenAPI schema to a C# type string.
    /// </summary>
    public string ResolveCSharpType(IOpenApiSchema schema, string? context = null)
    {
        if (++_ctx.RecursionDepth > MaxRecursionDepth)
        {
            _ctx.RecursionDepth--;
            return QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
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

    internal string? ResolveFormat(IOpenApiSchema schema)
    {
        if (schema.Format is not null)
        {
            return schema.Format;
        }

        if (
            schema is OpenApiSchemaReference { Reference.Id: { } refId }
            && _componentSchemas is not null
        )
        {
            var finalKey = _aliasTargets.GetValueOrDefault(refId, refId);
            if (
                _componentSchemas.TryGetValue(finalKey, out var target)
                && target is not OpenApiSchemaReference
            )
            {
                return ResolveFormat(target);
            }
        }

        foreach (var branches in new[] { schema.OneOf, schema.AnyOf })
        {
            if (branches is not { Count: 2 })
            {
                continue;
            }

            var valueBranch = branches.FirstOrDefault(branch =>
                branch.Type is not { } type || type != JsonSchemaType.Null
            );
            if (
                valueBranch is not null
                && branches.Any(branch => branch.Type == JsonSchemaType.Null)
            )
            {
                return ResolveFormat(valueBranch);
            }
        }

        if (schema.AllOf is [var only])
        {
            return ResolveFormat(only);
        }

        return null;
    }

    internal string? ResolveSchemaType(IOpenApiSchema schema)
    {
        if (schema.Type is { } declaredType)
        {
            var type = declaredType & ~JsonSchemaType.Null;
            var name = type switch
            {
                JsonSchemaType.String => "string",
                JsonSchemaType.Integer => "integer",
                JsonSchemaType.Number => "number",
                JsonSchemaType.Boolean => "boolean",
                JsonSchemaType.Object => "object",
                JsonSchemaType.Array => "array",
                _ => null,
            };
            if (name is not null)
            {
                return name;
            }
        }

        if (
            schema is OpenApiSchemaReference { Reference.Id: { } refId }
            && _componentSchemas is not null
        )
        {
            var finalKey = _aliasTargets.GetValueOrDefault(refId, refId);
            if (
                _componentSchemas.TryGetValue(finalKey, out var target)
                && target is not OpenApiSchemaReference
            )
            {
                return ResolveSchemaType(target);
            }
        }

        foreach (var branches in new[] { schema.OneOf, schema.AnyOf })
        {
            if (branches is not { Count: 2 })
            {
                continue;
            }

            var valueBranch = branches.FirstOrDefault(branch =>
                branch.Type is not { } type || type != JsonSchemaType.Null
            );
            if (
                valueBranch is not null
                && branches.Any(branch => branch.Type == JsonSchemaType.Null)
            )
            {
                return ResolveSchemaType(valueBranch);
            }
        }

        if (schema.AllOf is [var only])
        {
            return ResolveSchemaType(only);
        }

        return null;
    }

    private string ResolveCSharpTypeCore(IOpenApiSchema schema, string? context)
    {
        // $ref — try to resolve directly; if it's a primitive alias, fall through to type resolution
        if (
            schema is OpenApiSchemaReference schemaRef
            && TryResolveSchemaReference(schemaRef, context, out var refResult)
        )
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
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportAliasTargetMissing,
                            $"Alias schema '{key}' references missing schema '{targetId ?? "(null)"}' — consumers fall back to JsonElement."
                        )
                    );
                    _unresolvableAliases.Add(key);
                    break;
                }

                if (!visited.Add(targetId))
                {
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportAliasRefCycle,
                            $"Alias schema '{key}' is part of a $ref cycle ({string.Join(" -> ", visited)}) — consumers fall back to JsonElement."
                        )
                    );
                    _unresolvableAliases.Add(key);
                    break;
                }

                current = targetId;
            }
        }
    }

    /// <summary>
    /// P2 wave 4: detects oneOf schemas whose <c>discriminator.propertyName</c> +
    /// <c>mapping</c> can be reversed into a [JsonPolymorphic] base with
    /// [JsonDerivedType] registrations. A union qualifies only when every mapped
    /// variant resolves to a plain object component carrying a conforming tag
    /// property and the mapping covers the oneOf exactly; anything else records a
    /// rejection reason and keeps the ResolveUnionRecord fallback — loudly.
    /// </summary>
    private void DetectPolymorphicUnions(IDictionary<string, IOpenApiSchema> schemas)
    {
        foreach (var (key, schema) in schemas)
        {
            if (
                schema is OpenApiSchemaReference
                || schema.OneOf is not { Count: > 0 }
                || schema.Discriminator
                    is not { PropertyName: { } discriminatorProperty } discriminator
            )
            {
                continue;
            }

            if (discriminator.Mapping is not { Count: > 0 } mapping)
            {
                _polymorphicRejections[key] = "mapping absent";
                continue;
            }

            var failure = TryResolvePolymorphicVariants(
                key,
                discriminatorProperty,
                schema,
                mapping,
                schemas,
                out var variants
            );
            if (failure is not null)
            {
                _polymorphicRejections[key] = failure;
                continue;
            }

            _polymorphicBases[key] = new PolymorphicUnion(discriminatorProperty, variants);
            foreach (var (_, variantKey) in variants)
            {
                _polymorphicVariantKeys[variantKey] = key;
            }
        }
    }

    /// <summary>
    /// Validates and resolves a discriminator mapping's variants. Returns null on
    /// success (with <paramref name="variants"/> populated as tag → final schema key),
    /// or the human-readable rejection reason.
    /// </summary>
    private string? TryResolvePolymorphicVariants(
        string baseKey,
        string discriminatorProperty,
        IOpenApiSchema baseSchema,
        IDictionary<string, OpenApiSchemaReference> mapping,
        IDictionary<string, IOpenApiSchema> schemas,
        out List<(string Tag, string VariantKey)> variants
    )
    {
        variants = [];

        if (baseSchema.Properties is { Count: > 0 })
        {
            return "the schema declares sibling properties alongside oneOf";
        }

        var oneOfIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in baseSchema.OneOf!)
        {
            if (entry is not OpenApiSchemaReference { Reference.Id: { } entryId })
            {
                return "oneOf contains inline (non-$ref) variants";
            }

            oneOfIds.Add(entryId);
        }

        var mappedIds = new HashSet<string>(StringComparer.Ordinal);
        var claimedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tag, reference) in mapping)
        {
            if (reference.Reference?.Id is not { } refId)
            {
                return $"mapping entry '{tag}' is not a local schema reference";
            }

            mappedIds.Add(refId);

            var finalKey = _aliasTargets.GetValueOrDefault(refId, refId);
            if (!schemas.TryGetValue(finalKey, out var target) || target is OpenApiSchemaReference)
            {
                return $"mapping entry '{tag}' references unresolvable schema '{refId}'";
            }

            if (finalKey == baseKey)
            {
                return $"mapping entry '{tag}' references the union itself";
            }

            if (!claimedKeys.Add(finalKey))
            {
                return $"mapping maps multiple tags to schema '{refId}'";
            }

            if (
                _polymorphicVariantKeys.ContainsKey(finalKey)
                || _polymorphicBases.ContainsKey(finalKey)
            )
            {
                return $"schema '{refId}' is already part of another polymorphic union";
            }

            if (
                target.AllOf is { Count: > 0 }
                || target.OneOf is { Count: > 0 }
                || target.AnyOf is { Count: > 0 }
            )
            {
                return $"variant '{refId}' is a composition (allOf/oneOf/anyOf), not a plain object";
            }

            if (!VariantCarriesTag(target, discriminatorProperty, tag, schemas))
            {
                return $"variant '{refId}' has no conforming '{discriminatorProperty}' tag property";
            }

            variants.Add((tag, finalKey));
        }

        if (!oneOfIds.SetEquals(mappedIds))
        {
            return "mapping does not cover the oneOf variants exactly";
        }

        return null;
    }

    /// <summary>
    /// True when the variant is a plain object whose <paramref name="discriminatorProperty"/>
    /// is a string-typed property admitting <paramref name="tag"/> (any enum/const
    /// constraint must contain the tag).
    /// </summary>
    private bool VariantCarriesTag(
        IOpenApiSchema variant,
        string discriminatorProperty,
        string tag,
        IDictionary<string, IOpenApiSchema> schemas
    )
    {
        if (
            !SchemaClassifier.IsObject(variant)
            || variant.Properties is not { } properties
            || !properties.TryGetValue(discriminatorProperty, out var tagSchema)
        )
        {
            return false;
        }

        // The tag property may itself be a $ref (e.g. to a shared enum) — resolve it.
        if (tagSchema is OpenApiSchemaReference { Reference.Id: { } tagRefId })
        {
            var finalKey = _aliasTargets.GetValueOrDefault(tagRefId, tagRefId);
            if (
                !schemas.TryGetValue(finalKey, out var resolved)
                || resolved is OpenApiSchemaReference
            )
            {
                return false;
            }

            tagSchema = resolved;
        }

        if (tagSchema.Enum is { Count: > 0 } allowed)
        {
            return allowed.Any(v =>
                v?.GetValueKind() == System.Text.Json.JsonValueKind.String
                && v.GetValue<string>() == tag
            );
        }

        if (tagSchema.Const is { } constValue)
        {
            return constValue == tag;
        }

        return tagSchema.Type is { } type
            && (type & JsonSchemaType.String) == JsonSchemaType.String;
    }

    private bool TryResolveSchemaReference(
        OpenApiSchemaReference schemaRef,
        string? context,
        out string result
    )
    {
        result = "";

        var refId = DecodeComponentId(schemaRef.Reference.Id);

        // I1: refs to unresolvable aliases (cycle/missing target) — loud fallback,
        // and never touch the proxy (a cyclic chain overflows the stack)
        if (refId is not null && _unresolvableAliases.Contains(refId))
        {
            _ctx.Warnings.Add(
                Diagnostics.Prefix(
                    Diagnostics.ImportUnresolvableAliasReference,
                    $"Reference to unresolvable alias schema '{refId}'{(context is null ? "" : $" (in '{context}')")} — using JsonElement."
                )
            );
            result = QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
            return true;
        }

        // I1: refs to alias entries resolve against the FINAL target schema and name
        var effective = (IOpenApiSchema)schemaRef;
        var effectiveId = refId;
        if (
            refId is not null
            && _componentSchemas is not null
            && _componentSchemas.TryGetValue(refId, out var directTarget)
        )
        {
            effective = directTarget;
        }
        if (
            refId is not null
            && _aliasTargets.TryGetValue(refId, out var finalKey)
            && _componentSchemas is not null
            && _componentSchemas.TryGetValue(finalKey, out var finalSchema)
        )
        {
            effective = finalSchema;
            effectiveId = finalKey;
        }

        if (
            effectiveId is not null
            && _skippedComponentTypes.Contains(effectiveId)
            && effective.AllOf is [var skippedTarget]
        )
        {
            result = ResolveCSharpType(skippedTarget, context);
            return true;
        }

        // If the target is a property-less object schema, resolve to Dictionary
        // (no record was generated for it in MapSchemas) — unless marked as empty record
        if (SchemaClassifier.IsObject(effective) && effective.Properties is not { Count: > 0 })
        {
            if (
                effective.AdditionalProperties is null
                && effectiveId is not null
                && _ctx.SchemaNameMap.TryGetValue(effectiveId, out var emptyRecordName)
            )
            {
                result = emptyRecordName;
                return true;
            }

            result = ResolveObjectType(effective, context);
            return true;
        }

        // If the target has x-rivet-generic, resolve to generic type string
        if (SchemaClassifier.TryGetGenericExtension(effective, out var genericInfo))
        {
            result = SchemaClassifier.BuildGenericTypeString(genericInfo!);
            return true;
        }

        if (effectiveId is not null && TryMapNamedArray("_", effectiveId, effective, out _))
        {
            result = ResolveCSharpType(effective, context);
            return true;
        }

        // If the target would generate a type (record, enum, brand), use the mapped name
        // (alias-chased, dedup-aware). Otherwise it's a primitive alias — fall through to
        // resolve the underlying type.
        if (SchemaClassifier.WouldGenerateType(effective))
        {
            result =
                effectiveId is not null
                && _ctx.SchemaNameMap.TryGetValue(effectiveId, out var mapped)
                    ? mapped
                    : SanitizeName(effectiveId ?? schemaRef.Reference.Id!);

            // FABLE_ROUNDTRIP #6: a component that is itself nullable (3.0
            // `nullable: true` / 3.1 null in the type array — both parse to the
            // Null flag) makes every bare $ref use-site nullable. Dropping this
            // typed 139 github-corpus properties non-nullable that the API can
            // return as null — clients broke at runtime on an over-claim.
            if (effective.Type is { } targetType && targetType.HasFlag(JsonSchemaType.Null))
            {
                result += "?";
            }

            return true;
        }

        // Primitive alias — fall through to type resolution on the resolved schema
        return false;
    }

    private static string? DecodeComponentId(string? value) =>
        value
            ?.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);

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
            if (
                schema.AllOf is { Count: 1 }
                && schema.AllOf[0] is OpenApiSchemaReference nullableRef
                && schema.Properties is not { Count: > 0 }
            )
            {
                result = SanitizeName(nullableRef.Reference.Id!) + "?";
                return true;
            }

            if (schema.AllOf is { Count: > 0 })
            {
                var allOfName = context ?? _ctx.NextSyntheticName("Composed");
                var record = _synth.ResolveAllOfRecord(
                    allOfName,
                    schema.AllOf,
                    inheritedRequired: schema.Required
                );
                record = _synth.MergeWithSiblingProperties(record, schema, allOfName);
                result = _ctx.AddOrReuseExtraRecord(record) + "?";
                return true;
            }

            // x-rivet-csharp-type on nullable untyped schema
            var nullableCsharpType = SchemaClassifier.GetExtensionString(
                schema,
                "x-rivet-csharp-type"
            );
            if (nullableCsharpType is not null)
            {
                result =
                    QualifyFrameworkScalarIfShadowed(
                        SchemaClassifier.ResolveJsonNodeFqn(nullableCsharpType)
                    ) + "?";
                return true;
            }

            result = QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
            return true;
        }

        return false;
    }

    private bool TryResolveNullableOneOf(IOpenApiSchema schema, string? context, out string result)
    {
        result = "";

        // oneOf/anyOf with an explicit null branch (nullable ref/composite in 3.1)
        if (
            TryResolveTwoBranchNullable(schema.OneOf, context, out result)
            || TryResolveTwoBranchNullable(schema.AnyOf, context, out result)
        )
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
    private bool TryResolveTwoBranchNullable(
        IList<IOpenApiSchema>? branches,
        string? context,
        out string result
    )
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
            if (SchemaClassifier.IsNullOnlyBranch(item))
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
            // Some generators use allOf to attach annotations to one actual value
            // schema. Those annotation-only branches do not create an object carrier.
            var valueBranches = schema.AllOf.Where(ContributesRuntimeShape).ToList();
            var constrainedScalarBranches = schema
                .AllOf.Where(branch => branch.Enum is { Count: > 0 } || branch.Const is not null)
                .ToList();
            if (constrainedScalarBranches.Count == 1 && valueBranches.All(IsPrimitiveTypeBranch))
            {
                valueBranches = constrainedScalarBranches;
            }
            if (
                valueBranches.Count == 1
                && schema.Properties is not { Count: > 0 }
                && schema.Items is null
                && schema.AdditionalProperties is null
                && schema.AdditionalPropertiesAllowed
            )
            {
                result = ResolveCSharpType(valueBranches[0], context);
                return true;
            }

            var allOfName = context ?? _ctx.NextSyntheticName("Composed");
            var record = _synth.ResolveAllOfRecord(
                allOfName,
                schema.AllOf,
                inheritedRequired: schema.Required
            );
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

    private static bool ContributesRuntimeShape(IOpenApiSchema schema) =>
        schema is OpenApiSchemaReference
        || schema.Type is { } type && (type & ~JsonSchemaType.Null) != 0
        || schema.Properties is { Count: > 0 }
        || schema.Items is not null
        || schema.AdditionalProperties is not null
        || schema.AllOf is { Count: > 0 }
        || schema.OneOf is { Count: > 0 }
        || schema.AnyOf is { Count: > 0 }
        || schema.Required is { Count: > 0 }
        || schema.PatternProperties is { Count: > 0 }
        || schema.Discriminator is not null
        || !schema.AdditionalPropertiesAllowed;

    private static bool IsPrimitiveTypeBranch(IOpenApiSchema schema) =>
        schema.Type is { } type
        && (type & ~JsonSchemaType.Null)
            is JsonSchemaType.String
                or JsonSchemaType.Integer
                or JsonSchemaType.Number
                or JsonSchemaType.Boolean
        && schema.Properties is not { Count: > 0 }
        && schema.Items is null
        && schema.AdditionalProperties is null
        && schema.AllOf is not { Count: > 0 }
        && schema.OneOf is not { Count: > 0 }
        && schema.AnyOf is not { Count: > 0 };

    private string ResolveUnionType(string? context, IList<IOpenApiSchema> variants)
    {
        var name = context ?? _ctx.NextSyntheticName("Composed");
        var record = _synth.ResolveUnionRecord(name, variants);
        return _ctx.AddOrReuseExtraRecord(record);
    }

    private string ResolveFallbackType(IOpenApiSchema schema, string? context)
    {
        // enum without explicit type (common in some generators)
        if (schema.Enum is { Count: > 0 })
        {
            if (SchemaClassifier.IsIntEnum(schema))
            {
                return SynthesizeInlineIntEnum(schema, context);
            }

            if (SchemaClassifier.IsStringEnum(schema))
            {
                return SynthesizeInlineEnum(schema, context);
            }

            WarnEnumConstraintDropped(schema, context, "string");
            return "string";
        }

        // Inline object with properties but no type field (JSON Schema: properties implies object)
        if (schema.Properties is { Count: > 0 })
        {
            return ResolveObjectType(schema, context);
        }

        // JSON Schema permits structural array keywords without an explicit type.
        // Keep the exact typeless transport shape in generated provenance while using
        // the only runtime collection shape compatible with an authored items schema.
        if (schema.Items is not null)
        {
            return ResolveArrayType(schema, context);
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
            return QualifyFrameworkScalarIfShadowed(
                SchemaClassifier.ResolveJsonNodeFqn(untypedCsharpType)
            );
        }

        // Final fallback — only warn if the schema had structural properties we should have handled
        if (SchemaClassifier.HasResolvableProperties(schema))
        {
            return WarnAndFallback(
                Diagnostics.ImportUnresolvedSchema,
                "Schema could not be resolved to a C# type"
            );
        }

        return QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
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
            return QualifyFrameworkScalarIfShadowed(
                SchemaClassifier.ResolveJsonNodeFqn(csharpType)
            );
        }

        if (type.HasFlag(JsonSchemaType.String))
        {
            if (schema.Enum is { Count: > 0 })
            {
                return SynthesizeInlineEnum(schema, context);
            }

            var stringType = QualifyFrameworkScalarIfShadowed(
                SchemaClassifier.ResolveStringType(schema)
            );
            if (schema.Enum is { Count: > 0 })
            {
                WarnEnumConstraintDropped(schema, context, stringType);
            }

            return stringType;
        }

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            if (schema.Enum is { Count: > 0 } && SchemaClassifier.IsIntEnum(schema))
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

        return WarnAndFallback(
            Diagnostics.ImportUnsupportedSchemaType,
            $"Unsupported JSON Schema type '{type}'"
        );
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
        return QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
    }

    /// <summary>
    /// Named diagnostic (I.A-15): an enum constraint that cannot be represented as a C# enum
    /// (single value, mixed/float values, out-of-int32-range values) degrades to a primitive.
    /// Never silent — the values are dropped from the generated contract.
    /// </summary>
    private void WarnEnumConstraintDropped(
        IOpenApiSchema schema,
        string? context,
        string degradedTo
    )
    {
        var values = string.Join(", ", schema.Enum!.Select(v => v?.ToJsonString() ?? "null"));
        var where = context is not null ? $" at '{context}'" : "";
        _ctx.Warnings.Add(
            Diagnostics.Prefix(
                Diagnostics.ImportEnumConstraintDropped,
                $"Enum constraint dropped{where}: values [{values}] degraded to '{degradedTo}'."
            )
        );
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
        // Inline object with properties
        if (schema.Properties is { Count: > 0 })
        {
            var fingerprint = SchemaClassifier.ComputeSchemaFingerprint(schema);
            if (_ctx.SchemaFingerprints.TryGetValue(fingerprint, out var existingName))
            {
                return existingName;
            }

            var name = context ?? $"Synthetic{++_ctx.SyntheticCounter}";
            // Nullable and format are use-site facets for an inline helper. Stamping
            // them on the helper as well applies them twice when the helper is inlined;
            // other root metadata (title, required, constraints) still belongs to the
            // object shape and must remain on its generated definition.
            var record = _synth.MapRecord(name, schema);
            record = record with
            {
                SchemaMetadata = record
                    .SchemaMetadata?.Select(metadata =>
                        metadata.Pointer == ""
                            ? metadata with
                            {
                                Metadata = metadata.Metadata with
                                {
                                    Format = null,
                                    IsFormatSpecified = false,
                                    IsNullable = false,
                                },
                            }
                            : metadata
                    )
                    .ToList(),
            };
            var finalName = _ctx.AddOrReuseExtraRecord(record);
            _ctx.SchemaFingerprints[fingerprint] = finalName;
            return finalName;
        }

        if (schema.AdditionalProperties is not null)
        {
            var valueType = ResolveCSharpType(schema.AdditionalProperties, context);
            var keyType = ResolveDictionaryKeyType(schema, context);
            return $"Dictionary<{keyType}, {valueType}>";
        }

        // Bare object with no properties or additionalProperties → untyped map
        return $"Dictionary<string, {QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement")}>";
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
        if (
            schema.UnrecognizedKeywords is null
            || !schema.UnrecognizedKeywords.TryGetValue("propertyNames", out var node)
            || node is null
        )
        {
            return "string";
        }

        if (node is JsonObject obj)
        {
            // $ref → enum or string-backed brand component
            if (GetStringMember(obj, "$ref") is { } refValue)
            {
                const string prefix = "#/components/schemas/";
                if (
                    refValue.StartsWith(prefix, StringComparison.Ordinal)
                    && TryResolveComponentKeyType(refValue[prefix.Length..], out var keyName)
                )
                {
                    return keyName;
                }
            }
            else
            {
                // x-rivet-csharp-type pins the exact key type (numeric-keyed dictionaries etc.)
                if (GetStringMember(obj, "x-rivet-csharp-type") is { } csharpType)
                {
                    return QualifyFrameworkScalarIfShadowed(
                        SchemaClassifier.ResolveJsonNodeFqn(csharpType)
                    );
                }

                if (GetStringMember(obj, "type") == "string")
                {
                    var keyType = GetStringMember(obj, "format") switch
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
                    return QualifyFrameworkScalarIfShadowed(keyType);
                }
            }
        }

        var where = context is not null ? $" at '{context}'" : "";
        _ctx.Warnings.Add(
            Diagnostics.Prefix(
                Diagnostics.ImportDictionaryKeyDropped,
                $"propertyNames key schema{where} has no C# dictionary-key representation — imported with string keys."
            )
        );
        return "string";
    }

    private string QualifyFrameworkScalarIfShadowed(string typeName)
    {
        if (
            typeName.StartsWith("System.", StringComparison.Ordinal)
            && _ctx.ReservedTypeNames.Contains("System")
        )
        {
            return "global::" + typeName;
        }

        if (!_ctx.ReservedTypeNames.Contains(typeName))
        {
            return typeName;
        }

        return typeName switch
        {
            "DateTime" => "global::System.DateTime",
            "DateTimeOffset" => "global::System.DateTimeOffset",
            "DateOnly" => "global::System.DateOnly",
            "TimeOnly" => "global::System.TimeOnly",
            "Guid" => "global::System.Guid",
            "Uri" => "global::System.Uri",
            _ => typeName,
        };
    }

    private static string? GetStringMember(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node)
        && node is JsonValue value
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
        if (
            _componentSchemas is null
            || !_componentSchemas.TryGetValue(finalId, out var componentSchema)
            || !_ctx.SchemaNameMap.TryGetValue(finalId, out var mapped)
        )
        {
            return false;
        }

        if (
            SchemaClassifier.IsStringEnum(componentSchema)
            || (
                SchemaClassifier.IsBrand(componentSchema)
                && componentSchema.Type?.HasFlag(JsonSchemaType.String) == true
            )
        )
        {
            keyName = mapped;
            return true;
        }

        return false;
    }
}
