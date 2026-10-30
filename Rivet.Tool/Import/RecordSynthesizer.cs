using System.Globalization;
using Microsoft.OpenApi;
using Rivet.Tool.Analysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Import;

/// <summary>
/// Builds GeneratedRecord instances from OpenAPI composition schemas (allOf, oneOf, anyOf).
/// </summary>
internal sealed class RecordSynthesizer(ResolutionContext ctx, SchemaMapper mapper)
{
    public GeneratedRecord ResolveAllOfRecord(
        string name,
        IList<IOpenApiSchema> allOfList,
        HashSet<string>? visited = null,
        ISet<string>? inheritedRequired = null
    )
    {
        if (!ctx.Resolving.Add(name))
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
                var refName = ctx.TypeName(
                    SchemaMapper.DecodeComponentId(elementRef.Reference.Id)!
                );
                ctx.AllOfBranches.TryAdd(refName, name);

                if (visited.Contains(refName))
                {
                    continue;
                }

                // If the ref target itself has allOf, recurse — and merge the target's own
                // sibling properties too (middle inheritance layers contribute their
                // declared properties, not just their composed bases).
                if (element.AllOf is { Count: > 0 })
                {
                    var childRequired = UnionRequired(element.Required, inheritedRequired);
                    var nested = ResolveAllOfRecord(refName, element.AllOf, visited, childRequired);
                    nested = MergeWithSiblingProperties(
                        nested,
                        element,
                        refName,
                        inheritedRequired
                    );
                    props = nested.Properties.ToList();
                }
                else
                {
                    props = ExtractProperties(element, name, inheritedRequired);
                }
            }
            else
            {
                props = ExtractProperties(element, name, inheritedRequired);
            }

            foreach (var prop in props)
            {
                if (seenNames.Add(prop.Name))
                {
                    merged.Add(prop);
                }
            }
        }

        ctx.Resolving.Remove(name);
        return new GeneratedRecord(
            name,
            merged,
            HasExtensionData: allOfList.All(schema => schema.AdditionalPropertiesAllowed),
            DeclaresAdditionalProperties: allOfList.Any(
                SchemaClassifier.DeclaresAdditionalProperties
            )
        );
    }

    /// <summary>
    /// The <c>[RivetUnion]</c> wrapper for <paramref name="schema"/>'s oneOf or anyOf
    /// <paramref name="variants"/>. It is never closed: its members come from the variants
    /// (RIV3033).
    /// </summary>
    public GeneratedRecord ResolveUnionRecord(
        string name,
        IOpenApiSchema schema,
        IList<IOpenApiSchema> variants
    )
    {
        if (!schema.AdditionalPropertiesAllowed)
        {
            ctx.WarnClosedSchemaLeftOpen(schema, name, ResolutionContext.ClosedUnionReason);
        }

        var properties = new List<RecordProperty>();
        var optionIndex = 0;

        foreach (var variant in variants)
        {
            if (
                variant is OpenApiSchemaReference variantRef
                && SchemaClassifier.WouldGenerateType(variantRef)
            )
            {
                var refName = ctx.TypeName(
                    SchemaMapper.DecodeComponentId(variantRef.Reference.Id)!
                );
                properties.Add(new RecordProperty($"As{refName}", $"{refName}?", false));
            }
            else if (variant is not OpenApiSchemaReference && variant.Properties is { Count: > 0 })
            {
                // Inline object → synthesize sub-record (dedup-with-shape-check). A $ref
                // WouldGenerateType rejects reads its target's properties through the proxy,
                // so it resolves below to the type its component names instead.
                var subName = $"{name}Option{optionIndex}";
                var record = MapRecord(subName, variant);
                var finalSubName = ctx.AddOrReuseExtraRecord(record);
                properties.Add(new RecordProperty($"As{finalSubName}", $"{finalSubName}?", false));
                optionIndex++;
            }
            else
            {
                // Inline primitive
                var csharpType = mapper.ResolveCSharpType(variant, null);
                var nullableType = csharpType.EndsWith("?") ? csharpType : $"{csharpType}?";
                var typeName = SchemaClassifier.PrimitiveDisplayName(csharpType.TrimEnd('?'));
                properties.Add(new RecordProperty($"As{typeName}", nullableType, false));
            }
        }

        return new GeneratedRecord(
            name,
            SchemaClassifier.DeduplicateProperties(properties),
            IsUnion: true
        );
    }

    /// <summary>
    /// The record properties <paramref name="schema"/> declares, each type resolved (and any
    /// inline type synthesized) under <paramref name="context"/>. A property whose key is in
    /// <paramref name="skip"/> is left out before its type is resolved.
    /// </summary>
    public List<RecordProperty> ExtractProperties(
        IOpenApiSchema schema,
        string context,
        ISet<string>? inheritedRequired = null,
        IReadOnlySet<string>? skip = null
    )
    {
        var properties = new List<RecordProperty>();
        // A composing schema's top-level `required` applies to the whole composed
        // instance — union it with the element's own `required` so requiredness is
        // decided before the "?" suffix is appended.
        var requiredSet =
            UnionRequired(schema.Required, inheritedRequired)
            ?? (ISet<string>)new HashSet<string>();

        if (schema.Properties is not null)
        {
            foreach (var (propKey, propSchema) in schema.Properties)
            {
                if (skip?.Contains(propKey) == true)
                {
                    continue;
                }

                var propName = Naming.ToPascalCaseFromSegments(propKey);
                if (propName == context || Naming.IsReservedRecordMemberName(propName))
                {
                    propName += "Value";
                }

                var propContext = $"{context}{propName}";
                var isRequired = requiredSet.Contains(propKey);
                var csharpType = mapper.ResolveCSharpType(propSchema, propContext);
                var scalarReference = mapper.ResolveScalarReferenceName(propSchema);

                var isDeprecated = propSchema.Deprecated;

                // Preserve both an explicit format and its explicit absence. CLR
                // primitives otherwise invent defaults such as int64 and double.
                var format = mapper.ResolveFormat(propSchema);
                var isFormatSpecified =
                    format is not null || BuiltInScalars.HasImplicitFormat(csharpType.TrimEnd('?'));

                // Preserve default value
                string? defaultValue = null;
                if (propSchema.Default is not null)
                {
                    defaultValue = propSchema.Default.ToJsonString();
                }

                // Preserve constraints
                var constraints = ExtractConstraints(propSchema);

                // Preserve description, example, readOnly, writeOnly
                var description = string.IsNullOrEmpty(propSchema.Description)
                    ? null
                    : propSchema.Description;

                // 3.1 emits the JSON Schema 2020-12 `examples` array; legacy specs may
                // still carry the deprecated schema-level `example`.
                string? example = null;
                if (propSchema.Examples is { Count: > 0 } && propSchema.Examples[0] is not null)
                {
                    example = OpenApiJsonNodeSerializer.Serialize(propSchema.Examples[0]!);
                }
                else if (propSchema.Example is not null)
                {
                    example = OpenApiJsonNodeSerializer.Serialize(propSchema.Example);
                }

                var isReadOnly = propSchema.ReadOnly;
                var isWriteOnly = propSchema.WriteOnly;

                // A component $ref is the complete authored property schema. The OpenAPI
                // reader exposes resolved target facets through its proxy; emitting those
                // again as $ref siblings would invent use-site metadata.
                if (scalarReference is not null)
                {
                    isDeprecated = false;
                    format = null;
                    isFormatSpecified = false;
                    defaultValue = null;
                    constraints = null;
                    description = null;
                    example = null;
                    isReadOnly = false;
                    isWriteOnly = false;
                }

                // Wire fidelity: the walker re-emits camelCase(Name) unless a
                // [JsonPropertyName] says otherwise, and the runtime serializer
                // makes the same assumption — snake_case keys, already-PascalCase
                // keys, and renamed properties all need the original key pinned.
                var wireName = Naming.ToCamelCase(propName) == propKey ? null : propKey;

                properties.Add(
                    new RecordProperty(
                        propName,
                        csharpType,
                        isRequired,
                        isDeprecated,
                        format,
                        defaultValue,
                        constraints,
                        description,
                        example,
                        isReadOnly,
                        isWriteOnly,
                        WireName: wireName,
                        IsFormatSpecified: isFormatSpecified,
                        SchemaType: scalarReference is null
                            ? mapper.ResolveLeafSchemaType(propSchema, csharpType)
                            : null,
                        SchemaRef: scalarReference,
                        SchemaMetadata: SchemaMapper.CollectGeneratedSchemaMetadata(propSchema),
                        NestedSchemaRefs: scalarReference is null
                            ? mapper.CollectNestedScalarReferences(propSchema)
                            : null
                    )
                );
            }
        }

        return SchemaClassifier.DeduplicateProperties(properties);
    }

    private static ISet<string>? UnionRequired(ISet<string>? own, ISet<string>? inherited)
    {
        if (inherited is not { Count: > 0 })
        {
            return own;
        }

        if (own is not { Count: > 0 })
        {
            return inherited;
        }

        var union = new HashSet<string>(own);
        union.UnionWith(inherited);
        return union;
    }

    /// <summary>
    /// <paramref name="record"/>, an allOf composition's merged branches, with
    /// <paramref name="schema"/>'s own sibling properties added. A composition is never closed:
    /// allOf applies a closed part's <c>additionalProperties: false</c> to the members every
    /// other part adds, so the schema contradicts itself (RIV3033).
    /// </summary>
    public GeneratedRecord MergeWithSiblingProperties(
        GeneratedRecord record,
        IOpenApiSchema schema,
        string name,
        ISet<string>? inheritedRequired = null
    )
    {
        if (
            !schema.AdditionalPropertiesAllowed
            || schema.AllOf?.Any(branch =>
                branch is not OpenApiSchemaReference && !branch.AdditionalPropertiesAllowed
            ) == true
        )
        {
            ctx.WarnClosedSchemaLeftOpen(schema, name, ResolutionContext.ClosedAllOfReason);
        }

        if (schema.Properties is not { Count: > 0 })
        {
            return record with
            {
                Name = name,
                HasExtensionData = record.HasExtensionData && schema.AdditionalPropertiesAllowed,
                DeclaresAdditionalProperties =
                    record.DeclaresAdditionalProperties
                    || SchemaClassifier.DeclaresAdditionalProperties(schema),
            };
        }

        var siblingProps = ExtractProperties(schema, name, inheritedRequired);
        var merged = record.Properties.ToList();
        var seen = new HashSet<string>(merged.Select(p => p.Name));
        foreach (var prop in siblingProps)
        {
            if (seen.Add(prop.Name))
            {
                merged.Add(prop);
            }
        }

        return record with
        {
            Name = name,
            Properties = merged,
            HasExtensionData = record.HasExtensionData && schema.AdditionalPropertiesAllowed,
            DeclaresAdditionalProperties =
                record.DeclaresAdditionalProperties
                || SchemaClassifier.DeclaresAdditionalProperties(schema),
        };
    }

    /// <summary>
    /// The record <paramref name="schema"/> maps to. A property whose key is in
    /// <paramref name="skip"/> is left out before its type is resolved, so no inline type is
    /// synthesized for it.
    /// </summary>
    public GeneratedRecord MapRecord(
        string name,
        IOpenApiSchema schema,
        IReadOnlySet<string>? skip = null
    )
    {
        if (!ctx.Resolving.Add(name))
        {
            return new GeneratedRecord(name, []);
        }

        var properties = ExtractProperties(schema, name, skip: skip);
        var description = string.IsNullOrEmpty(schema.Description) ? null : schema.Description;
        ctx.Resolving.Remove(name);
        return new GeneratedRecord(
            name,
            properties,
            Description: description,
            SchemaMetadata: SchemaMapper.CollectGeneratedSchemaMetadata(schema),
            HasExtensionData: schema.AdditionalPropertiesAllowed,
            DeclaresAdditionalProperties: SchemaClassifier.DeclaresAdditionalProperties(schema),
            IsClosed: !schema.AdditionalPropertiesAllowed
        );
    }

    public GeneratedRecord? BuildGenericTemplateRecord(
        string templateName,
        GenericTemplateInfo info,
        IDictionary<string, IOpenApiSchema> schemas
    )
    {
        // The first monomorphised instance derives the template properties; the template is
        // closed only when every instance is, since one C# type serves them all.
        var instances = schemas
            .Where(entry =>
                SchemaClassifier.TryGetGenericExtension(entry.Value, out var schemaInfo)
                && schemaInfo!.Name == templateName
            )
            .ToList();
        if (instances.Count == 0)
        {
            return null;
        }

        var firstInstance = instances[0].Value;
        var openInstances = instances
            .Where(entry => entry.Value.AdditionalPropertiesAllowed)
            .Select(entry => $"'{entry.Key}'")
            .ToList();
        if (openInstances.Count > 0)
        {
            foreach (
                var (key, _) in instances.Where(entry => !entry.Value.AdditionalPropertiesAllowed)
            )
            {
                ctx.WarnClosedSchemaLeftOpen(
                    key,
                    $"it is an instance of the generic template '{templateName}', which serves "
                        + $"{string.Join(", ", openInstances)} too, and "
                        + (openInstances.Count == 1 ? "that one isn't" : "those aren't")
                        + " closed"
                );
            }
        }

        // Extract properties from the monomorphised instance
        var monoProps = ExtractProperties(firstInstance, templateName);

        // Reverse-substitute concrete types back to type params using the args map
        var reverseMap = info.Args.ToDictionary(kv => kv.Value, kv => kv.Key);
        var templateProps = new List<RecordProperty>();
        foreach (var prop in monoProps)
        {
            var templatedType = SchemaClassifier.ReverseSubstituteTypes(
                prop.CSharpType,
                reverseMap
            );
            // A member re-typed to a type parameter is no longer the instance's scalar, so the
            // instance's [RivetSchemaType] goes with it (the walker refuses it on `T`, RIV1117).
            templateProps.Add(
                prop with
                {
                    CSharpType = templatedType,
                    SchemaType = mapper.IsBuiltInScalar(templatedType) ? prop.SchemaType : null,
                }
            );
        }

        return new GeneratedRecord(
            templateName,
            templateProps,
            info.TypeParams,
            HasExtensionData: firstInstance.AdditionalPropertiesAllowed,
            DeclaresAdditionalProperties: SchemaClassifier.DeclaresAdditionalProperties(
                firstInstance
            ),
            IsClosed: openInstances.Count == 0
        );
    }

    internal static TsPropertyConstraints? ExtractConstraints(IOpenApiSchema schema)
    {
        var c = new TsPropertyConstraints(
            MinLength: schema.MinLength.HasValue ? (int)schema.MinLength.Value : null,
            MaxLength: schema.MaxLength.HasValue ? (int)schema.MaxLength.Value : null,
            Pattern: schema.Pattern,
            Minimum: Number(schema.Minimum),
            Maximum: Number(schema.Maximum),
            ExclusiveMinimum: Number(schema.ExclusiveMinimum),
            ExclusiveMaximum: Number(schema.ExclusiveMaximum),
            MultipleOf: (double?)schema.MultipleOf,
            MinItems: schema.MinItems.HasValue ? (int)schema.MinItems.Value : null,
            MaxItems: schema.MaxItems.HasValue ? (int)schema.MaxItems.Value : null,
            UniqueItems: schema.UniqueItems == true ? true : null,
            MinProperties: schema.MinProperties,
            MaxProperties: schema.MaxProperties
        );

        return c.HasAny ? c : null;

        static double? Number(string? value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }
}
