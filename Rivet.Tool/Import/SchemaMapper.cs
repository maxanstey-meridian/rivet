using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
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

    // Component alias resolution ("Alias": {"$ref": "#/components/schemas/Real"}).
    // Alias keys map to their FINAL (non-reference) target key; chains to a missing target
    // are recorded separately so consumers fall back loudly instead of chasing the
    // library's reference proxies.
    private readonly Dictionary<string, string> _aliasTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvableAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skippedComponentTypes = new(StringComparer.Ordinal);
    private IDictionary<string, IOpenApiSchema>? _componentSchemas;
    private readonly HashSet<string> _unsupportedNamedScalarsWarned = new(StringComparer.Ordinal);
    private readonly HashSet<IOpenApiSchema> _unheldIntegerEnumsWarned = [];
    private readonly HashSet<string> _requiredComponentSchemas = new(StringComparer.Ordinal);

    // A oneOf + discriminator + usable mapping reverses to an abstract
    // [JsonPolymorphic] base record with [JsonDerivedType] registrations. Bases are
    // keyed by schema key; each conforming variant key maps back to its base key and its
    // tag; bases whose mapping could NOT be reversed record the reason for the loud
    // RIV3005 fallback warning.
    private readonly Dictionary<string, PolymorphicUnion> _polymorphicBases = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, (string BaseKey, string Tag)> _polymorphicVariantKeys = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, string> _polymorphicRejections = new(
        StringComparer.Ordinal
    );

    private sealed record PolymorphicUnion(
        string DiscriminatorProperty,
        IReadOnlyList<(string Tag, string VariantKey)> Variants
    );

    // OpenAPI 3.0's allOf inheritance: a discriminator on a base with no oneOf names the
    // allOf descendants by mapping entry or, implicitly, by component name. Every member
    // with descendants (and every concrete one, so a leaf written on its own still carries
    // its tag) becomes a [JsonPolymorphic] record inheriting from its parent's record.
    private readonly Dictionary<string, HierarchyMember> _hierarchyMembers = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, string> _hierarchyRejections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuiltHierarchyRecord> _hierarchyRecords = new(
        StringComparer.Ordinal
    );

    private sealed record HierarchyMember(
        string DiscriminatorProperty,
        string? ParentKey,
        string Tag,
        // Itself first when concrete, then every concrete descendant.
        IReadOnlyList<string> RegisteredKeys,
        // Every property it or an ancestor lists in `required`: allOf applies them all.
        IReadOnlySet<string> Required
    );

    // Effective: every member the record has, its own and inherited, keyed by JSON name,
    // with the member that declares it and the property schema it was last declared by.
    private sealed record BuiltHierarchyRecord(
        GeneratedRecord Record,
        IReadOnlyDictionary<
            string,
            (RecordProperty Property, string Owner, IOpenApiSchema? Source)
        > Effective
    );

    public SchemaMapper(List<string> warnings)
    {
        _ctx = new ResolutionContext(warnings);
        _synth = new RecordSynthesizer(_ctx, this);
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
    /// Registers a synthetic record (e.g. a parameter input record built by ContractBuilder).
    /// An identical shape reuses the existing record; a name collision with a different shape
    /// gets a suffixed name. Returns the name to reference.
    /// </summary>
    public string AddExtraRecord(GeneratedRecord record) => _ctx.AddOrReuseExtraRecord(record);

    /// <summary>
    /// The header-augmented replacement for a component record, or null when
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
            // A polymorphic record's wire shape isn't its own property list: it adds the tag
            // and, in an allOf hierarchy, everything it inherits. A oneOf variant's adds the
            // tag too.
            || record.Polymorphism is not null
            || record.BaseTypeName is not null
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
                // A header-bound property is a different shape from a plain
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
    /// Header-aware component reuse for synthesized inputs. [RivetHeader]
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
    /// emit∘import a fixed point. Null when nothing matches.
    /// </summary>
    public string? FindNumberedSchemaWithShape(
        string baseName,
        IReadOnlyList<RecordProperty> properties
    ) =>
        ComponentCandidates(baseName)
            .Skip(1)
            .FirstOrDefault(name => HasMappedSchemaWithShape(name, properties));

    /// <summary>The N of a <see cref="Naming.NameCandidates"/> name <c>{baseName}_N</c>.</summary>
    private static int? ParseNumberedSuffix(string name, string baseName)
    {
        var prefix = baseName + "_";
        return
            name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(prefix.Length), out var suffix)
            ? suffix
            : null;
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

        // Resolve alias chains first, using raw reference ids only (never proxied
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
            // Alias entries are resolved via _aliasTargets; touching their proxied
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
        // name with a different shape (two types in one Types/{Name}.cs file).
        foreach (var (key, schema) in schemas)
        {
            // Aliases produce no file of their own and must NOT claim a name — they
            // map to their target's name in the follow-up loop below
            if (schema is OpenApiSchemaReference)
            {
                continue;
            }

            // Deduplicate schema names that collide after PascalCase sanitization
            var name = Naming.UniqueName(_ctx.TypeName(key), usedNames);

            // Track mapping from original OpenAPI key to (possibly deduped) C# name
            _ctx.SchemaNameMap[key] = name;
            _ctx.ReservedTypeNames.Add(name);
        }

        // Alias keys map to the FINAL target's mapped name so every consumer of the
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
                var name = Naming.UniqueName(_ctx.TypeName(key), usedNames);
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

        // Detect reversible polymorphic hierarchies and unions BEFORE mapping so members
        // can be generated as derived records regardless of iteration order. Hierarchies
        // first: a oneOf can't claim a variant whose record already has an allOf parent.
        // Hierarchy records are built during detection, after the skips above.
        DetectPolymorphicHierarchies(schemas);
        DetectPolymorphicUnions(schemas);

        foreach (var (key, schema) in schemas)
        {
            // Skip $ref aliases — resolved via the alias-target map; unresolvable
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

            if (SchemaClassifier.IsTitledConstEnum(schema))
            {
                enums.Add(SchemaClassifier.MapTitledConstEnum(name, schema));
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

            // A schema claimed as a polymorphic union variant becomes a
            // derived record: the discriminator property is STRIPPED (System.Text.Json
            // re-adds it on the wire — keeping it would double-emit) and the record
            // inherits from the abstract base.
            if (_polymorphicVariantKeys.TryGetValue(key, out var variantOf))
            {
                var discriminatorProperty = _polymorphicBases[
                    variantOf.BaseKey
                ].DiscriminatorProperty;
                // An allOf variant (a $ref'd base plus its own object) flattens like any
                // allOf component; the union base is its only C# parent.
                var derived = schema.AllOf is { Count: > 0 }
                    ? MapAllOfRecord(name, schema)
                    : _synth.MapRecord(name, schema);

                derived = derived with
                {
                    Properties = WithoutWireProperty(derived.Properties, discriminatorProperty),
                    BaseTypeName = _ctx.SchemaNameMap[variantOf.BaseKey],
                    Description = schemaDescription,
                    DiscriminatorSchemaJson = AuthoredTagSchemaJson(
                        schema,
                        discriminatorProperty,
                        variantOf.Tag
                    ),
                };
                records.Add(derived);
                continue;
            }

            if (_hierarchyRecords.TryGetValue(key, out var hierarchyRecord))
            {
                records.Add(hierarchyRecord.Record);
                continue;
            }

            if (schema.AllOf is { Count: > 0 })
            {
                // A discriminator beside an allOf can't root a hierarchy: the root is the one
                // member with no parent of its own. One whose allOf nests a union of its own
                // (Cloudflare's dns-record-response) dispatches over that union instead.
                if (schema.Discriminator?.PropertyName is { } allOfDiscriminator)
                {
                    var nestsUnion = schema.AllOf.Any(part =>
                        part is not OpenApiSchemaReference
                        && (part.OneOf is { Count: > 0 } || part.AnyOf is { Count: > 0 })
                    );
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportDiscriminatorDropped,
                            $"Discriminator dropped on '{name}': property '{allOfDiscriminator}' "
                                + (
                                    nestsUnion
                                        ? "dispatches over a oneOf/anyOf nested in its allOf, which Rivet can't reverse"
                                        : "sits beside an allOf of its own, so it can't root a hierarchy"
                                )
                                + " — imported as a flattened record."
                        )
                    );
                }

                var record = MapAllOfRecord(name, schema);

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

                // A oneOf + discriminator + usable mapping reverses to an
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
                            ),
                            SchemaMetadata: CollectGeneratedSchemaMetadata(schema)
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
                var propertyless = schema.Properties is not { Count: > 0 };

                // Named diagnostic: a discriminator on a plain object schema with neither a
                // oneOf union nor an allOf hierarchy to dispatch over has no C# contract
                // representation — the record is generated but the dispatch semantics are
                // dropped. A root that declares no properties (its descendants declare the
                // tag) is no quieter about it.
                if (schema.Discriminator?.PropertyName is { } discriminatorProperty)
                {
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportDiscriminatorDropped,
                            $"Discriminator dropped on '{name}': property '{discriminatorProperty}' "
                                + (
                                    _hierarchyRejections.TryGetValue(key, out var rejection)
                                        ? $"has no usable allOf hierarchy ({rejection})"
                                        : "has no oneOf union and no allOf descendants"
                                )
                                + (
                                    propertyless && schema.AdditionalProperties is not null
                                        ? " — imported as a dictionary."
                                        : " — imported as a regular record."
                                )
                        )
                    );
                }

                if (propertyless)
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

        // Register component records for shape-checked reuse
        var componentIdsByName = _ctx
            .SchemaNameMap.Where(pair => schemas[pair.Key] is not OpenApiSchemaReference)
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

        records = records
            .Select(record =>
                componentIdsByName.TryGetValue(record.Name, out var id)
                    ? record with
                    {
                        ComponentId = id,
                    }
                    : record
            )
            .ToList();
        enums = enums
            .Select(value =>
                componentIdsByName.TryGetValue(value.Name, out var id)
                    ? value with
                    {
                        ComponentId = id,
                    }
                    : value
            )
            .ToList();
        brands = brands
            .Select(value =>
                componentIdsByName.TryGetValue(value.Name, out var id)
                    ? value with
                    {
                        ComponentId = id,
                    }
                    : value
            )
            .ToList();

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
        if ((schema.Type ?? LiteralEnumNullableType(schema)) is not { } declared)
        {
            return false;
        }

        var nonNull = declared & ~JsonSchemaType.Null;
        var schemaType = SchemaClassifier.ScalarTypeKeyword(nonNull);
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

        if (SchemaClassifier.IsTitledConstEnum(schema))
        {
            scalar = new GeneratedScalarSchema(
                name,
                componentId,
                schemaType,
                schema.Format,
                declared.HasFlag(JsonSchemaType.Null),
                BuildScalarMetadata(schema, declared.HasFlag(JsonSchemaType.Null)),
                IsEnum: true
            );
            return true;
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
                "const or schema composition; only primitive leaves and flat enums are supported"
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

        var metadata = BuildScalarMetadata(schema, declared.HasFlag(JsonSchemaType.Null));
        var isEnum = SchemaClassifier.IsStringEnum(schema) || SchemaClassifier.IsIntEnum(schema);
        if (!isEnum && declared.HasFlag(JsonSchemaType.Integer))
        {
            WarnUnheldIntegerEnumValues(
                schema,
                IntegerCarrier(schema),
                $"component '{componentId}'"
            );
        }
        scalar = new GeneratedScalarSchema(
            name,
            componentId,
            schemaType,
            schema.Format,
            declared.HasFlag(JsonSchemaType.Null),
            metadata,
            IsEnum: isEnum
        );
        return true;
    }

    private static bool IsEnumValueOfType(JsonNode? value, JsonSchemaType type) =>
        value is JsonValue scalar
        && scalar.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => type
                == JsonSchemaType.Boolean,
            System.Text.Json.JsonValueKind.Number => type == JsonSchemaType.Number
                || type == JsonSchemaType.Integer && IsWholeNumber(scalar, out _),
            _ => false,
        };

    /// <summary>
    /// Whether a JSON number is whole at any magnitude (<c>1e400</c> counts, as JSON Schema
    /// says), read exactly from its literal text.
    /// </summary>
    private static bool IsWholeNumber(JsonValue value, out BigInteger number)
    {
        number = default;
        return value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && BigInteger.TryParse(
                value.ToJsonString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number
            );
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
            || SchemaClassifier.IsStringEnum(schema)
            || SchemaClassifier.IsIntEnum(schema)
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
                || ((schema.Type ?? LiteralEnumType(schema)) & ~JsonSchemaType.Null)
                    is JsonSchemaType.Integer
                        or JsonSchemaType.Number,
            Required: schema.Required is { Count: > 0 } ? schema.Required.ToList() : null,
            Enum: LiteralEnumJson(schema),
            EnumListsNull: EnumListsNull(schema)
        );

    private static bool EnumListsNull(IOpenApiSchema schema) =>
        schema.Extensions?.ContainsKey(OpenApiImporter.ImportedEnumNullMemberExtension) == true;

    private static string? LiteralEnumJson(IOpenApiSchema schema)
    {
        if (IsLiteralEnum(schema))
        {
            return new JsonArray([
                .. schema.Enum!.Select(value => value?.DeepClone()),
            ]).ToJsonString();
        }
        // The reader drops null members (EnumListsNull puts them back on emit), so a
        // null-only enum arrives empty and only the marker says it was there.
        return schema.Enum is not { Count: > 0 } && EnumListsNull(schema) ? "[null]" : null;
    }

    /// <summary>
    /// An enum no generated C# enum carries (booleans, numbers outside the integer enums,
    /// arrays, objects, null). The C# type follows the values and the values themselves
    /// ride beside it as generated metadata.
    /// </summary>
    private static bool IsLiteralEnum(IOpenApiSchema schema) =>
        schema.Enum is { Count: > 0 }
        && !SchemaClassifier.IsStringEnum(schema)
        && !SchemaClassifier.IsIntEnum(schema);

    /// <summary>
    /// The JSON type an untyped literal enum's values share: boolean, integer or number.
    /// Null for arrays, objects, null and mixed values, which only JsonElement holds.
    /// </summary>
    private static JsonSchemaType? LiteralEnumType(IOpenApiSchema schema)
    {
        if (!IsLiteralEnum(schema))
        {
            return null;
        }
        JsonSchemaType[] candidates =
        [
            JsonSchemaType.Boolean,
            JsonSchemaType.Integer,
            JsonSchemaType.Number,
        ];
        return candidates
            .Where(type => schema.Enum!.All(value => IsEnumValueOfType(value, type)))
            .Cast<JsonSchemaType?>()
            .FirstOrDefault();
    }

    /// <summary>
    /// <see cref="LiteralEnumType"/> plus null when the enum also lists null: the reader
    /// drops null members and leaves only the marker, so <c>[true, null]</c> arrives as
    /// <c>[true]</c> and would otherwise infer a boolean that refuses null.
    /// </summary>
    private static JsonSchemaType? LiteralEnumNullableType(IOpenApiSchema schema) =>
        LiteralEnumType(schema) is not { } inferred ? null
        : EnumListsNull(schema) ? inferred | JsonSchemaType.Null
        : inferred;

    private static bool HasExplicitNullBranch(IOpenApiSchema schema) =>
        new[] { schema.OneOf, schema.AnyOf }.Any(branches =>
            branches is { Count: 2 } && branches.Any(branch => branch.Type == JsonSchemaType.Null)
        );

    internal static IReadOnlyList<GeneratedSchemaMetadata> CollectGeneratedSchemaMetadata(
        IOpenApiSchema schema
    )
    {
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
        // A $ref node is the referenced component's identity; its metadata belongs to
        // the component, and copying it to the use site wraps the $ref on emission.
        if (schema is OpenApiSchemaReference || !visited.Add(schema))
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
        || metadata.Xml is not null
        || metadata.Enum is not null
        || metadata.EnumListsNull;

    /// <summary>
    /// An integer literal enum keeps its values as metadata whatever its C# type, but a whole
    /// value outside that type's range (<c>[-1, 18446744073709551615]</c> as a long) can't
    /// be received, so the import says which.
    /// </summary>
    private void WarnUnheldIntegerEnumValues(IOpenApiSchema schema, string carrier, string site)
    {
        // A $ref use site leaves it to the component, which warns for itself.
        if (
            schema is OpenApiSchemaReference
            || schema.Enum is not { Count: > 0 }
            || !_unheldIntegerEnumsWarned.Add(schema)
        )
        {
            return;
        }
        if (IntegerCarrierRange(carrier) is not var (min, max))
        {
            return;
        }
        var unheld = schema
            .Enum.OfType<JsonValue>()
            .Where(value => IsWholeNumber(value, out var number) && (number < min || number > max))
            .Select(value => value.ToJsonString())
            .ToList();
        if (unheld.Count == 0)
        {
            return;
        }
        _ctx.Warnings.Add(
            Diagnostics.Prefix(
                Diagnostics.ImportIntegerEnumValueUnheld,
                $"Integer enum at {site} lists {(unheld.Count == 1 ? "a value" : "values")} its C# type '{carrier}' can't hold ({string.Join(", ", unheld)}); the values stay in the spec, but C# code using that type can't read or write "
                    + (unheld.Count == 1 ? "it." : "them.")
            )
        );
    }

    private static string InlineSite(string? context) =>
        context is null ? "an inline schema" : $"'{context}'";

    /// <summary>
    /// The C# type an integer schema maps to: <c>x-rivet-csharp-type</c> when present, as
    /// <see cref="ResolveSingleType"/> resolves it, otherwise the one its format picks.
    /// </summary>
    private static string IntegerCarrier(IOpenApiSchema schema) =>
        SchemaClassifier.GetExtensionString(schema.Extensions, "x-rivet-csharp-type")
        ?? SchemaClassifier.ResolveIntegerType(schema);

    /// <summary>
    /// The range of a CLR integer keyword, or null for any other type (<c>Int128</c>,
    /// <c>decimal</c>), whose range the import doesn't check.
    /// </summary>
    private static (BigInteger Min, BigInteger Max)? IntegerCarrierRange(string carrier) =>
        carrier switch
        {
            "sbyte" => (sbyte.MinValue, sbyte.MaxValue),
            "byte" => (byte.MinValue, byte.MaxValue),
            "short" => (short.MinValue, short.MaxValue),
            "ushort" => (ushort.MinValue, ushort.MaxValue),
            "int" => (int.MinValue, int.MaxValue),
            "uint" => (uint.MinValue, uint.MaxValue),
            "long" => (long.MinValue, long.MaxValue),
            "ulong" => (ulong.MinValue, ulong.MaxValue),
            _ => null,
        };

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

    /// <summary>
    /// Named scalar references below a property schema, keyed by the JSON pointer the
    /// generated-schema metadata uses. Stops at a reference: its target owns the rest.
    /// </summary>
    internal IReadOnlyList<(string Pointer, string Name)>? CollectNestedScalarReferences(
        IOpenApiSchema schema
    )
    {
        var result = new List<(string Pointer, string Name)>();
        Collect(schema, "");
        return result.Count > 0 ? result : null;

        void Collect(IOpenApiSchema node, string pointer)
        {
            if (node is OpenApiSchemaReference)
            {
                return;
            }
            foreach (
                var (child, childPointer) in new[]
                {
                    (node.Items, pointer + "/items"),
                    (node.AdditionalProperties, pointer + "/additionalProperties"),
                }
            )
            {
                if (child is null)
                {
                    continue;
                }
                if (ResolveScalarReferenceName(child) is { } name)
                {
                    result.Add((childPointer, name));
                }
                else
                {
                    Collect(child, childPointer);
                }
            }
        }
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
                && !TryMapNamedUntyped("_", componentId, target, out _)
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

    internal string? ResolveFormat(IOpenApiSchema schema) =>
        schema.Format ?? (Unwrap(schema) is { } inner ? ResolveFormat(inner) : null);

    internal string? ResolveSchemaType(IOpenApiSchema schema) =>
        (schema.Type is { } type ? SchemaClassifier.TypeKeyword(type) : null)
        ?? (Unwrap(schema) is { } inner ? ResolveSchemaType(inner) : null);

    /// <summary>
    /// The schema that carries <paramref name="schema"/>'s leaf metadata: a component
    /// reference's (alias-chased) target, the value branch of a two-branch nullable
    /// oneOf/anyOf, or a single allOf entry. Null when there is nothing to look through.
    /// </summary>
    private IOpenApiSchema? Unwrap(IOpenApiSchema schema)
    {
        if (
            schema is OpenApiSchemaReference { Reference.Id: { } refId }
            && TryResolveComponent(refId, out var target)
        )
        {
            return target;
        }

        foreach (var branches in new[] { schema.OneOf, schema.AnyOf })
        {
            if (
                branches is { Count: 2 }
                && branches.Any(branch => branch.Type == JsonSchemaType.Null)
                && branches.FirstOrDefault(branch =>
                    branch.Type is not { } type || type != JsonSchemaType.Null
                )
                    is { } valueBranch
            )
            {
                return valueBranch;
            }
        }

        return schema.AllOf is [var only] ? only : null;
    }

    /// <summary>The non-alias component a reference id resolves to, chasing aliases.</summary>
    private bool TryResolveComponent(string refId, [NotNullWhen(true)] out IOpenApiSchema? target)
    {
        target = null;
        var componentId = DecodeComponentId(refId)!;
        return _componentSchemas is not null
            && _componentSchemas.TryGetValue(
                _aliasTargets.GetValueOrDefault(componentId, componentId),
                out target
            )
            && target is not OpenApiSchemaReference;
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
    /// Maps each $ref alias component to the non-alias component at the end of its chain.
    /// Alias cycles never reach here: <c>OpenApiImporter.BreakAliasCycles</c> replaces them
    /// before the document is parsed.
    /// </summary>
    private void ResolveAliasTargets(IDictionary<string, IOpenApiSchema> schemas)
    {
        foreach (var (key, schema) in schemas)
        {
            if (schema is not OpenApiSchemaReference)
            {
                continue;
            }

            string? current = key;
            while (schemas[current] is OpenApiSchemaReference reference)
            {
                var targetId = DecodeComponentId(reference.Reference.Id);
                if (targetId is null || !schemas.ContainsKey(targetId))
                {
                    _ctx.Warnings.Add(
                        Diagnostics.Prefix(
                            Diagnostics.ImportAliasTargetMissing,
                            $"Alias schema '{key}' references missing schema '{targetId ?? "(null)"}' — consumers fall back to JsonElement."
                        )
                    );
                    _unresolvableAliases.Add(key);
                    current = null;
                    break;
                }

                current = targetId;
            }

            if (current is not null)
            {
                _aliasTargets[key] = current;
            }
        }
    }

    /// <summary>
    /// Detects OpenAPI 3.0 allOf inheritance under a discriminator with no oneOf (Bitbucket's
    /// <c>object</c>). A component joins its parent's hierarchy through an allOf with exactly
    /// one <c>$ref</c>; its tag is its mapping entry, else its component name. A member is
    /// concrete when every declaration of the discriminator property down its chain admits
    /// its own tag (and, for an implicit tag, one of them constrains the value). A member that
    /// is neither concrete nor an ancestor of a concrete one can never be named by a payload
    /// (Bitbucket's <c>pipeline_selector</c> redefines <c>type</c> as a selector kind), so it
    /// stays a plain record outside the hierarchy. A hierarchy the C# can't hold is rejected
    /// while it is resolved, before anything is registered, with a reason for the loud
    /// RIV3005 fallback. Every surviving member's record is then built here, before anything
    /// else is mapped, so membership is settled before a oneOf could claim a member or a
    /// <c>$ref</c> resolve to one, and a record built early can't name a member that later
    /// leaves.
    /// </summary>
    private void DetectPolymorphicHierarchies(IDictionary<string, IOpenApiSchema> schemas)
    {
        // A descendant with one $ref joins its parent's hierarchy. One with several, or one
        // that also composes a oneOf/anyOf, can't be a derived record: it rejects the
        // hierarchy it reaches, by name.
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var composedChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unionChildren = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, schema) in schemas)
        {
            if (schema is OpenApiSchemaReference || schema.AllOf is not { Count: > 0 } parts)
            {
                continue;
            }

            var parentKeys = parts
                .OfType<OpenApiSchemaReference>()
                .Select(part => DecodeComponentId(part.Reference.Id))
                .Where(id => id is not null)
                .Select(id => _aliasTargets.GetValueOrDefault(id!, id!))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var into =
                schema.OneOf is { Count: > 0 } || schema.AnyOf is { Count: > 0 } ? unionChildren
                : parentKeys.Count == 1 ? children
                : composedChildren;
            foreach (var parentKey in parentKeys)
            {
                if (!into.TryGetValue(parentKey, out var list))
                {
                    into[parentKey] = list = [];
                }

                list.Add(key);
            }
        }

        // Every hierarchy's members are registered before any record is built, so a $ref to
        // a member resolves to its record whichever hierarchy is built first.
        var registered = new List<string>();
        foreach (var (rootKey, root) in schemas)
        {
            if (
                root is OpenApiSchemaReference
                || root.Discriminator is not { PropertyName: { } property } discriminator
                || root.OneOf is { Count: > 0 }
                || root.AnyOf is { Count: > 0 }
                || root.AllOf is { Count: > 0 }
                || !children.ContainsKey(rootKey)
                    && !composedChildren.ContainsKey(rootKey)
                    && !unionChildren.ContainsKey(rootKey)
            )
            {
                continue;
            }

            if (
                TryResolveHierarchy(
                    rootKey,
                    property,
                    discriminator.Mapping,
                    schemas,
                    children,
                    composedChildren,
                    unionChildren,
                    out var members
                ) is
                { } failure
            )
            {
                _hierarchyRejections[rootKey] = failure;
                continue;
            }

            foreach (var (key, member) in members)
            {
                _hierarchyMembers[key] = member;
                registered.Add(key);
            }
        }

        // Preorder: a parent's record is built before its children's.
        foreach (var key in registered)
        {
            BuildHierarchyRecord(key, schemas);
        }
    }

    private string? TryResolveHierarchy(
        string rootKey,
        string property,
        IDictionary<string, OpenApiSchemaReference>? mapping,
        IDictionary<string, IOpenApiSchema> schemas,
        Dictionary<string, List<string>> children,
        Dictionary<string, List<string>> composedChildren,
        Dictionary<string, List<string>> unionChildren,
        out List<(string Key, HierarchyMember Member)> members
    )
    {
        members = [];

        // Preorder: a parent always precedes its descendants.
        var order = new List<(string Key, string? Parent)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(rootKey, null);

        foreach (var (key, _) in order)
        {
            if (composedChildren.GetValueOrDefault(key) is [var composed, ..])
            {
                return $"'{composed}' extends '{key}' and other schemas through allOf, which C# single inheritance can't hold";
            }

            if (unionChildren.GetValueOrDefault(key) is [var union, ..])
            {
                return $"'{union}' extends '{key}' through allOf and composes a oneOf/anyOf as well, which a derived record can't hold";
            }

            if (
                OwnParts(schemas[key])
                    .Select(part => part.Discriminator?.PropertyName)
                    .FirstOrDefault(other => other is not null && other != property) is
                { } other
            )
            {
                return $"'{key}' declares a discriminator of its own, '{other}'";
            }
        }

        var parents = order.ToDictionary(
            entry => entry.Key,
            entry => entry.Parent,
            StringComparer.Ordinal
        );

        var tags = order.ToDictionary(
            entry => entry.Key,
            entry => entry.Key,
            StringComparer.Ordinal
        );
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var (tag, reference) in mapping ?? new Dictionary<string, OpenApiSchemaReference>()
        )
        {
            if (DecodeComponentId(reference.Reference?.Id) is not { } refId)
            {
                return $"mapping entry '{tag}' is not a local schema reference";
            }

            var target = _aliasTargets.GetValueOrDefault(refId, refId);
            if (!tags.ContainsKey(target))
            {
                return $"mapping entry '{tag}' references '{refId}', which doesn't extend '{rootKey}' through allOf";
            }

            if (!mapped.Add(target))
            {
                return $"mapping maps multiple tags to schema '{refId}'";
            }

            tags[target] = tag;
        }

        if (
            tags
                .Values.GroupBy(tag => tag, StringComparer.Ordinal)
                .FirstOrDefault(g => g.Count() > 1) is
            { } clash
        )
        {
            return $"tag '{clash.Key}' names two schemas";
        }

        // A member below the root may restate the discriminator with a mapping of its own.
        // The root's tags are the ones registered, so the member's mapping must agree.
        foreach (var (key, _) in order.Skip(1))
        {
            foreach (
                var (tag, reference) in OwnParts(schemas[key])
                    .SelectMany(part =>
                        part.Discriminator?.Mapping
                        ?? new Dictionary<string, OpenApiSchemaReference>()
                    )
            )
            {
                var refId = DecodeComponentId(reference.Reference?.Id);
                var target = refId is null ? null : _aliasTargets.GetValueOrDefault(refId, refId);
                if (target is null || !tags.TryGetValue(target, out var computed))
                {
                    return $"'{key}' maps tag '{tag}' to '{refId ?? reference.Reference?.Id}', which isn't in the hierarchy";
                }

                if (computed != tag)
                {
                    return $"'{key}' maps tag '{tag}' to '{refId}', which the hierarchy tags '{computed}'";
                }
            }
        }

        // Every declaration down the chain applies at once, so each must admit the tag. A
        // mapped tag is named explicitly, so that is enough. An implicit tag (the component
        // name) also needs the chain to constrain the value somewhere: a chain that says
        // nothing, or only documents the property, names no payload.
        var concrete = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, _) in order)
        {
            var tag = tags[key];
            var declarations = new List<IOpenApiSchema>();
            for (string? link = key; link is not null; link = parents[link])
            {
                declarations.AddRange(OwnTagDeclarations(schemas[link], property));
            }

            var admitted =
                declarations.All(declaration => AdmitsTag(declaration, tag))
                && (mapped.Contains(key) || declarations.Any(ConstrainsTag));

            if (!admitted && mapped.Contains(key))
            {
                return $"mapping entry '{tag}' names '{key}', whose '{property}' doesn't admit it";
            }

            concrete[key] = admitted;
        }

        // Postorder: a member stays when it is concrete or has a member below it.
        var included = new HashSet<string>(StringComparer.Ordinal);
        for (var index = order.Count - 1; index >= 0; index--)
        {
            var key = order[index].Key;
            if (concrete[key] || children.GetValueOrDefault(key)?.Any(included.Contains) == true)
            {
                included.Add(key);
            }
        }

        if (!included.Contains(rootKey))
        {
            return $"no allOf descendant carries a '{property}' tag it admits";
        }

        // What building a member's record would trip over, checked here so that building a
        // registered hierarchy can't fail. Extension data lives on the root, and a member
        // that refuses extra properties can't un-inherit its dictionary. C# can't hide a
        // required member, so a member may restate one only as the schema it inherits, give
        // or take documentation. This errs loud: a restatement that differs in schema but
        // would map to the same C# member still rejects the hierarchy.
        var required = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sources = new Dictionary<string, Dictionary<string, IOpenApiSchema>>(
            StringComparer.Ordinal
        );
        foreach (var (key, parent) in order.Where(entry => included.Contains(entry.Key)))
        {
            var parts = OwnParts(schemas[key]);
            var own = parts
                .SelectMany(part => part.Required ?? new HashSet<string>())
                .ToHashSet(StringComparer.Ordinal);
            var declared = DeclaredProperties(parts);
            declared.Remove(property);
            if (parent is null)
            {
                required[key] = own;
                sources[key] = declared;
                continue;
            }

            if (
                schemas[rootKey].AdditionalPropertiesAllowed
                && !parts.All(part => part.AdditionalPropertiesAllowed)
            )
            {
                return $"'{key}' refuses additional properties its ancestor '{rootKey}' accepts";
            }

            var effective = new Dictionary<string, IOpenApiSchema>(
                sources[parent],
                StringComparer.Ordinal
            );
            foreach (var (name, source) in declared)
            {
                if (sources[parent].TryGetValue(name, out var inherited))
                {
                    if (
                        SchemaClassifier.ComputeUndocumentedFingerprint(source)
                        == SchemaClassifier.ComputeUndocumentedFingerprint(inherited)
                    )
                    {
                        continue;
                    }

                    if (required[parent].Contains(name))
                    {
                        var requirer = parent;
                        while (
                            !OwnParts(schemas[requirer])
                                .Any(part => part.Required?.Contains(name) == true)
                        )
                        {
                            requirer = parents[requirer]!;
                        }

                        return $"'{key}' redeclares '{name}', which '{requirer}' requires, and C# can't hide a required member";
                    }
                }

                effective[name] = source;
            }

            own.UnionWith(required[parent]);
            required[key] = own;
            sources[key] = effective;
        }

        foreach (var (key, parent) in order.Where(entry => included.Contains(entry.Key)))
        {
            var registered = new List<string>();
            CollectConcrete(key);
            members.Add(
                (key, new HierarchyMember(property, parent, tags[key], registered, required[key]))
            );

            void CollectConcrete(string current)
            {
                if (concrete[current])
                {
                    registered.Add(current);
                }

                foreach (var child in children.GetValueOrDefault(current) ?? [])
                {
                    if (included.Contains(child))
                    {
                        CollectConcrete(child);
                    }
                }
            }
        }

        return null;

        void Visit(string key, string? parent)
        {
            if (!seen.Add(key))
            {
                return;
            }

            order.Add((key, parent));
            foreach (var child in children.GetValueOrDefault(key) ?? [])
            {
                Visit(child, key);
            }
        }
    }

    /// <summary>
    /// What a schema says for itself in an allOf: its inline (non-<c>$ref</c>) parts and
    /// its own keywords, not what it inherits through a <c>$ref</c>.
    /// </summary>
    private static List<IOpenApiSchema> OwnParts(IOpenApiSchema schema) =>
        [.. schema.AllOf?.Where(part => part is not OpenApiSchemaReference) ?? [], schema];

    /// <summary>
    /// A hierarchy member's own declarations of the discriminator property: its properties
    /// and its inline allOf parts', not its parent's.
    /// </summary>
    private static IEnumerable<IOpenApiSchema> OwnTagDeclarations(
        IOpenApiSchema schema,
        string property
    ) =>
        OwnParts(schema)
            .Select(part =>
                part.Properties?.TryGetValue(property, out var declared) == true ? declared : null
            )
            .OfType<IOpenApiSchema>();

    /// <summary>
    /// The properties an allOf member's own parts declare, keyed by JSON name. The first
    /// declaration wins across parts, as in a flattened allOf record.
    /// </summary>
    private static Dictionary<string, IOpenApiSchema> DeclaredProperties(
        List<IOpenApiSchema> parts
    ) =>
        parts
            .SelectMany(part => part.Properties ?? new Dictionary<string, IOpenApiSchema>())
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

    /// <summary>
    /// An allOf component flattened into one record: its parts' properties, <c>$ref</c>'d
    /// parts copied rather than inherited, then its own sibling properties.
    /// </summary>
    private GeneratedRecord MapAllOfRecord(string name, IOpenApiSchema schema) =>
        _synth.MergeWithSiblingProperties(
            _synth.ResolveAllOfRecord(name, schema.AllOf!, inheritedRequired: schema.Required),
            schema,
            name
        ) with
        {
            SchemaMetadata = CollectGeneratedSchemaMetadata(schema),
        };

    /// <summary>
    /// Builds a hierarchy member's record into <see cref="_hierarchyRecords"/>. The root keeps
    /// its own properties. Every other member mentions a property by declaring it or by
    /// listing it in its own <c>required</c>, and allOf applies every <c>required</c> at
    /// once, so a property is required when the member or an ancestor requires it. A
    /// declaration whose schema differs from the inherited one only in documentation is the
    /// inherited member. A mentioned member that differs from the one it inherits
    /// (<see cref="SameMember"/>) is redeclared with <c>new</c>; the inherited one is optional,
    /// since <see cref="TryResolveHierarchy"/> rejected every hierarchy where it isn't. The
    /// discriminator property is stripped everywhere — System.Text.Json writes it. Extension
    /// data lives on the root only.
    /// </summary>
    private void BuildHierarchyRecord(string key, IDictionary<string, IOpenApiSchema> schemas)
    {
        var member = _hierarchyMembers[key];
        var schema = schemas[key];
        var name = _ctx.SchemaNameMap[key];
        var polymorphism = new PolymorphismInfo(
            member.DiscriminatorProperty,
            member
                .RegisteredKeys.Select(registered => new PolymorphicVariantRef(
                    _ctx.SchemaNameMap[registered],
                    _hierarchyMembers[registered].Tag
                ))
                .ToList()
        );

        if (member.ParentKey is not { } parentKey)
        {
            // The tag property is skipped before its type is resolved: System.Text.Json writes
            // it, and an inline enum synthesized for it would be an orphan.
            var root = _synth.MapRecord(
                name,
                schema,
                skip: new HashSet<string>(StringComparer.Ordinal) { member.DiscriminatorProperty }
            ) with
            {
                Polymorphism = polymorphism,
            };
            _hierarchyRecords[key] = new BuiltHierarchyRecord(
                root,
                root.Properties.ToDictionary(
                    property => property.JsonName,
                    property =>
                        (
                            property,
                            key,
                            schema.Properties?.TryGetValue(property.JsonName, out var source)
                            == true
                                ? source
                                : null
                        ),
                    StringComparer.Ordinal
                )
            );
            return;
        }

        var parent = _hierarchyRecords[parentKey];
        var parts = OwnParts(schema);
        var required = member.Required.ToHashSet(StringComparer.Ordinal);
        var sources = DeclaredProperties(parts);
        // A restatement that differs from the inherited declaration only in documentation is
        // the inherited member. It isn't extracted, so no inline type is synthesized for it: a
        // child's own description would otherwise give its inline enum a type of its own. The
        // tag property is skipped the same way.
        var skip = sources
            .Where(entry =>
                parent.Effective.GetValueOrDefault(entry.Key).Source is { } inherited
                && SchemaClassifier.ComputeUndocumentedFingerprint(entry.Value)
                    == SchemaClassifier.ComputeUndocumentedFingerprint(inherited)
            )
            .Select(entry => entry.Key)
            .Append(member.DiscriminatorProperty)
            .ToHashSet(StringComparer.Ordinal);
        var own = parts
            .SelectMany(part => _synth.ExtractProperties(part, name, required, skip: skip))
            .GroupBy(property => property.JsonName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var mentioned = own
            .Values.Select(property =>
                parent.Effective.GetValueOrDefault(property.JsonName).Property?.IsRequired == true
                    ? property with
                    {
                        IsRequired = true,
                    }
                    : property
            )
            .Concat(
                parent
                    .Effective.Values.Where(inherited =>
                        required.Contains(inherited.Property.JsonName)
                        && !own.ContainsKey(inherited.Property.JsonName)
                    )
                    .Select(inherited => inherited.Property with { IsRequired = true })
            );

        var declared = new List<RecordProperty>();
        var effective = new Dictionary<
            string,
            (RecordProperty Property, string Owner, IOpenApiSchema? Source)
        >(parent.Effective, StringComparer.Ordinal);
        foreach (var property in mentioned)
        {
            // A member required here but declared above keeps the declaration it inherits.
            var source = sources.GetValueOrDefault(property.JsonName);
            if (parent.Effective.TryGetValue(property.JsonName, out var inherited))
            {
                source ??= inherited.Source;
                if (SameMember(property, inherited.Property))
                {
                    // Still the inherited member, but restated: descendants compare against
                    // the restatement, as TryResolveHierarchy does.
                    effective[property.JsonName] = inherited with
                    {
                        Source = source,
                    };
                    continue;
                }

                if (inherited.Property.IsRequired)
                {
                    throw new UnreachableException(
                        $"'{key}' redeclares required '{property.JsonName}', which TryResolveHierarchy rejects."
                    );
                }

                declared.Add(property with { HidesInherited = true });
            }
            else
            {
                declared.Add(property);
            }

            effective[property.JsonName] = (property, key, source);
        }

        _hierarchyRecords[key] = new BuiltHierarchyRecord(
            new GeneratedRecord(
                name,
                declared,
                Description: string.IsNullOrEmpty(schema.Description) ? null : schema.Description,
                Polymorphism: polymorphism,
                BaseTypeName: _ctx.SchemaNameMap[parentKey],
                SchemaMetadata: CollectGeneratedSchemaMetadata(schema)
            ),
            effective
        );
    }

    /// <summary>
    /// True when two declarations of one property make the same C# member. Documentation
    /// (descriptions, titles, examples) doesn't count: a derived record can't restate it
    /// without hiding the member, and the member's preserved schema keeps it anyway.
    /// </summary>
    private static bool SameMember(RecordProperty a, RecordProperty b) =>
        Undocumented(a) == Undocumented(b)
        && (a.NestedSchemaRefs ?? []).SequenceEqual(b.NestedSchemaRefs ?? [])
        && UndocumentedMetadata(a.SchemaMetadata)
            .SequenceEqual(UndocumentedMetadata(b.SchemaMetadata));

    private static RecordProperty Undocumented(RecordProperty property) =>
        property with
        {
            Description = null,
            Example = null,
            HidesInherited = false,
            SchemaMetadata = null,
            NestedSchemaRefs = null,
        };

    private static IEnumerable<(
        string Pointer,
        TsScalarMetadata Metadata,
        string Required
    )> UndocumentedMetadata(IReadOnlyList<GeneratedSchemaMetadata>? metadata) =>
        (metadata ?? [])
            .Select(entry =>
                (
                    entry.Pointer,
                    Metadata: entry.Metadata with
                    {
                        Description = null,
                        Title = null,
                        Example = null,
                        Examples = null,
                    }
                )
            )
            .Where(entry => HasGeneratedSchemaMetadata(entry.Metadata))
            .Select(entry =>
                (
                    entry.Pointer,
                    entry.Metadata with
                    {
                        Required = null,
                    },
                    string.Join('\0', entry.Metadata.Required ?? [])
                )
            );

    /// <summary>
    /// Detects oneOf schemas whose <c>discriminator.propertyName</c> +
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
            foreach (var (tag, variantKey) in variants)
            {
                _polymorphicVariantKeys[variantKey] = (key, tag);
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

        // Siblings that only restate the discriminator (Jira's CustomContextVariable) are the
        // tag every variant carries anyway; anything else is a base-members-plus-oneOf shape
        // System.Text.Json has no form for.
        if (
            baseSchema.Properties is { Count: > 0 } siblings
            && (siblings.Count > 1 || !siblings.ContainsKey(discriminatorProperty))
        )
        {
            return "the schema declares sibling properties alongside oneOf";
        }

        if (
            baseSchema.Required?.Where(name => name != discriminatorProperty).ToList() is
            { Count: > 0 } requiredSiblings
        )
        {
            return $"the schema requires {string.Join(", ", requiredSiblings.Select(name => $"'{name}'"))} beside oneOf";
        }

        var siblingTag =
            baseSchema.Properties?.TryGetValue(discriminatorProperty, out var sibling) == true
                ? sibling
                : null;

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

            if (_hierarchyMembers.TryGetValue(finalKey, out var hierarchyMember))
            {
                return hierarchyMember.ParentKey is { } hierarchyParent
                    ? $"'{refId}' extends the discriminated base '{hierarchyParent}' through allOf"
                    : $"'{refId}' is the discriminated base of an allOf hierarchy";
            }

            if (
                _polymorphicVariantKeys.ContainsKey(finalKey)
                || _polymorphicBases.ContainsKey(finalKey)
            )
            {
                return $"schema '{refId}' is already part of another polymorphic union";
            }

            // An allOf variant flattens into one record (its $ref'd parts are copied, not
            // inherited: the union base is the record's only parent), so only a nested
            // oneOf/anyOf leaves it without a single object shape.
            if (
                target.OneOf is { Count: > 0 }
                || target.AnyOf is { Count: > 0 }
                || target.AllOf?.Any(part =>
                    part.OneOf is { Count: > 0 } || part.AnyOf is { Count: > 0 }
                ) == true
            )
            {
                return $"variant '{refId}' is a composition (oneOf/anyOf), not an object";
            }

            if (
                TagDeclarations(target, discriminatorProperty) is not { Count: > 0 } declared
                || !declared.All(declaration => AdmitsTag(declaration, tag))
            )
            {
                return $"variant '{refId}' has no conforming '{discriminatorProperty}' tag property";
            }

            if (siblingTag is not null && !AdmitsTag(siblingTag, tag))
            {
                return $"the sibling '{discriminatorProperty}' schema doesn't admit tag '{tag}'";
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
    /// Every schema that declares <paramref name="property"/> for <paramref name="schema"/>:
    /// its own properties and, through allOf, each part's ($ref'd parts followed to their
    /// component). allOf means all of them apply at once. Empty when a schema with no allOf
    /// isn't an object or nothing declares the property.
    /// </summary>
    private List<IOpenApiSchema> TagDeclarations(IOpenApiSchema schema, string property)
    {
        var declarations = new List<IOpenApiSchema>();
        if (schema.AllOf is not { Count: > 0 } && !SchemaClassifier.IsObject(schema))
        {
            return declarations;
        }

        Collect(schema, []);
        return declarations;

        void Collect(IOpenApiSchema current, HashSet<IOpenApiSchema> visited)
        {
            if (current is OpenApiSchemaReference { Reference.Id: { } id })
            {
                if (!TryResolveComponent(id, out var target))
                {
                    return;
                }

                current = target;
            }

            if (!visited.Add(current))
            {
                return;
            }

            if (current.Properties?.TryGetValue(property, out var declared) == true)
            {
                declarations.Add(declared);
            }

            foreach (var part in current.AllOf ?? [])
            {
                Collect(part, visited);
            }
        }
    }

    /// <summary>
    /// True when a tag property schema admits <paramref name="tag"/>, by the rule the walker
    /// holds an authored tag schema to (<see cref="TsType.TaggedUnionVariant.TagSchemaProblem"/>),
    /// so the importer never writes one the next generation refuses: every <c>enum</c>,
    /// <c>const</c> and <c>type</c> it has admits the tag, and one with none of them admits
    /// any tag, as JSON Schema says. That fits a tag a mapping names explicitly. The schema
    /// may itself be a <c>$ref</c> (e.g. to a shared enum).
    /// </summary>
    private bool AdmitsTag(IOpenApiSchema tagSchema, string tag) =>
        ResolveTagSchema(tagSchema) is { } resolved
        && TsType.TaggedUnionVariant.TagSchemaProblem(
            SchemaClassifier.SerializeAsV31(resolved),
            tag
        )
            is null;

    /// <summary>
    /// True when a tag property schema constrains the value with an <c>enum</c>,
    /// <c>const</c> or <c>type</c>. An implicit tag (an allOf member's component name) needs
    /// one somewhere down its chain: a chain whose tag property says nothing, or only
    /// documents it, says nothing about which payloads name the member, so it is abstract.
    /// </summary>
    private bool ConstrainsTag(IOpenApiSchema tagSchema) =>
        ResolveTagSchema(tagSchema) is { } resolved
        && (
            resolved.Enum is { Count: > 0 }
            || resolved.Const is not null
            || resolved.Type is not null
        );

    private IOpenApiSchema? ResolveTagSchema(IOpenApiSchema tagSchema) =>
        tagSchema is not OpenApiSchemaReference { Reference.Id: { } tagRefId } ? tagSchema
        : TryResolveComponent(tagRefId, out var resolved) ? resolved
        : null;

    /// <summary>
    /// The variant's own tag property schema as the source wrote it (Jira's
    /// <c>{ "type": "string", "description": … }</c>), or null when it is exactly the
    /// single-value enum the emitter synthesizes anyway.
    /// </summary>
    private static string? AuthoredTagSchemaJson(
        IOpenApiSchema variant,
        string property,
        string tag
    )
    {
        if (variant.Properties?.TryGetValue(property, out var declared) != true)
        {
            return null;
        }

        var authored = JsonNode.Parse(SchemaClassifier.SerializeAsV31(declared!))!;
        var synthesized = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(tag) };
        return JsonNode.DeepEquals(authored, synthesized) ? null : authored.ToJsonString();
    }

    /// <summary>The properties minus the one whose JSON name is <paramref name="wireName"/>.</summary>
    private static List<RecordProperty> WithoutWireProperty(
        IEnumerable<RecordProperty> properties,
        string wireName
    ) => properties.Where(property => property.JsonName != wireName).ToList();

    private bool TryResolveSchemaReference(
        OpenApiSchemaReference schemaRef,
        string? context,
        out string result
    )
    {
        result = "";

        var refId = DecodeComponentId(schemaRef.Reference.Id);

        // Refs to unresolvable aliases (missing target) — loud fallback,
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

        // Refs to alias entries resolve against the FINAL target schema and name
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

        // A hierarchy member is a record whatever it declares: its descendants derive from
        // it, and the payloads it stands for are its own members. Typeless, empty or
        // property-less, it still resolves to its record.
        if (
            effectiveId is not null
            && _hierarchyMembers.ContainsKey(effectiveId)
            && _ctx.SchemaNameMap.TryGetValue(effectiveId, out var memberName)
        )
        {
            result = AdmitsNull(effective) ? memberName + "?" : memberName;
            return true;
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
                effectiveId is not null
                && effective.AdditionalProperties is null
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
                    : _ctx.TypeName(effectiveId ?? DecodeComponentId(schemaRef.Reference.Id)!);

            // A component that is itself nullable (3.0
            // `nullable: true` / 3.1 null in the type array — both parse to the
            // Null flag) makes every bare $ref use-site nullable. Dropping this
            // typed 139 github-corpus properties non-nullable that the API can
            // return as null — clients broke at runtime on an over-claim.
            if (AdmitsNull(effective))
            {
                result += "?";
            }

            return true;
        }

        // Primitive alias — fall through to type resolution on the resolved schema
        return false;
    }

    /// <summary>
    /// True when a component is itself nullable (3.0 <c>nullable: true</c> and 3.1 null in
    /// the type array both parse to the Null flag), so every bare <c>$ref</c> to it is too.
    /// </summary>
    private static bool AdmitsNull(IOpenApiSchema schema) =>
        schema.Type is { } type && type.HasFlag(JsonSchemaType.Null);

    internal static string? DecodeComponentId(string? value) =>
        value is null ? null : JsonPointer.Unescape(Uri.UnescapeDataString(value));

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
                result = _ctx.TypeName(DecodeComponentId(nullableRef.Reference.Id)!) + "?";
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
                schema.Extensions,
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
                return SynthesizeInlineEnum(schema, context, SchemaClassifier.MapIntEnum);
            }

            if (SchemaClassifier.IsStringEnum(schema))
            {
                return SynthesizeInlineEnum(schema, context, SchemaClassifier.MapEnum);
            }

            // Items say what each array value is; the enum still rides as generated metadata.
            // A non-array member (true, 1, {}) lies outside what items constrains, so only an
            // all-array enum is a list.
            if (schema.Items is not null && schema.Enum.All(value => value is JsonArray))
            {
                return ResolveArrayType(schema, context) + (EnumListsNull(schema) ? "?" : "");
            }

            return LiteralEnumType(schema) is { } inferred
                ? ResolveSingleType(inferred, schema, context) + (EnumListsNull(schema) ? "?" : "")
                : QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
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
            // An enum that lists only null arrives here with its members stripped.
            return ResolveArrayType(schema, context) + (EnumListsNull(schema) ? "?" : "");
        }

        // const without type — infer from the const value
        if (schema.Const is not null)
        {
            return ResolveConstType(schema.Const);
        }

        // x-rivet-csharp-type on untyped schemas (e.g. JsonNode, JsonObject, JsonArray)
        var untypedCsharpType = SchemaClassifier.GetExtensionString(
            schema.Extensions,
            "x-rivet-csharp-type"
        );
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

        if (int.TryParse(constValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "int";
        }

        if (
            double.TryParse(
                constValue,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out _
            )
        )
        {
            return "double";
        }

        return "string";
    }

    // --- Type resolution helpers ---

    private string ResolveSingleType(JsonSchemaType type, IOpenApiSchema schema, string? context)
    {
        // x-rivet-csharp-type takes precedence — exact C# type for lossless round-trips
        var csharpType = SchemaClassifier.GetExtensionString(
            schema.Extensions,
            "x-rivet-csharp-type"
        );
        if (csharpType is not null)
        {
            if (type.HasFlag(JsonSchemaType.Integer))
            {
                WarnUnheldIntegerEnumValues(schema, csharpType, InlineSite(context));
            }
            return QualifyFrameworkScalarIfShadowed(
                SchemaClassifier.ResolveJsonNodeFqn(csharpType)
            );
        }

        if (type.HasFlag(JsonSchemaType.String))
        {
            if (schema.Enum is { Count: > 0 })
            {
                return SynthesizeInlineEnum(schema, context, SchemaClassifier.MapEnum);
            }

            return QualifyFrameworkScalarIfShadowed(SchemaClassifier.ResolveStringType(schema));
        }

        if (type.HasFlag(JsonSchemaType.Integer))
        {
            if (schema.Enum is { Count: > 0 } && SchemaClassifier.IsIntEnum(schema))
            {
                return SynthesizeInlineEnum(schema, context, SchemaClassifier.MapIntEnum);
            }

            var carrier = SchemaClassifier.ResolveIntegerType(schema);
            WarnUnheldIntegerEnumValues(schema, carrier, InlineSite(context));
            return carrier;
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

    private string WarnAndFallback(string diagnosticId, string reason)
    {
        _ctx.Warnings.Add(Diagnostics.Prefix(diagnosticId, $"{reason} — mapped to 'JsonElement'."));
        return QualifyFrameworkScalarIfShadowed("System.Text.Json.JsonElement");
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

    private string SynthesizeInlineEnum(
        IOpenApiSchema schema,
        string? context,
        Func<string, IOpenApiSchema, GeneratedEnum> map
    )
    {
        var fingerprint = SchemaClassifier.ComputeSchemaFingerprint(schema);
        if (_ctx.SchemaFingerprints.TryGetValue(fingerprint, out var existingName))
        {
            return existingName;
        }

        var name = context ?? _ctx.NextSyntheticName("Enum");
        var finalName = _ctx.AddOrReuseExtraEnum(map(name, schema));
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
