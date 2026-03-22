using Microsoft.OpenApi;

namespace Rivet.Tool.Import;

/// <summary>
/// Builds GeneratedRecord instances from OpenAPI composition schemas (allOf, oneOf, anyOf).
/// Receives a type resolution callback to avoid circular dependency with SchemaMapper.
/// </summary>
internal sealed class RecordSynthesizer(
    ResolutionContext ctx,
    Func<IOpenApiSchema, string?, string> resolveType)
{
    public GeneratedRecord ResolveAllOfRecord(string name, IList<IOpenApiSchema> allOfList, HashSet<string>? visited = null)
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

        ctx.Resolving.Remove(name);
        return new GeneratedRecord(name, merged);
    }

    public GeneratedRecord ResolveUnionRecord(string name, IList<IOpenApiSchema> variants)
    {
        var properties = new List<RecordProperty>();
        var optionIndex = 0;

        foreach (var variant in variants)
        {
            if (variant is OpenApiSchemaReference variantRef && SchemaClassifier.WouldGenerateType(variantRef))
            {
                var refName = SanitizeName(variantRef.Reference.Id!);
                properties.Add(new RecordProperty($"As{refName}", $"{refName}?", false));
            }
            else if (variant.Properties is { Count: > 0 })
            {
                // Inline object → synthesize sub-record
                var subName = $"{name}Option{optionIndex}";
                var record = MapRecord(subName, variant);
                ctx.ExtraRecords.Add(record);
                properties.Add(new RecordProperty($"As{subName}", $"{subName}?", false));
                optionIndex++;
            }
            else
            {
                // Inline primitive
                var csharpType = resolveType(variant, null);
                var nullableType = csharpType.EndsWith("?") ? csharpType : $"{csharpType}?";
                var typeName = SchemaClassifier.PrimitiveDisplayName(csharpType.TrimEnd('?'));
                properties.Add(new RecordProperty($"As{typeName}", nullableType, false));
            }
        }

        return new GeneratedRecord(name, SchemaClassifier.DeduplicateProperties(properties));
    }

    public List<RecordProperty> ExtractProperties(IOpenApiSchema schema, string context)
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

                var propContext = $"{context}{propName}";
                var isRequired = requiredSet.Contains(propKey);
                var csharpType = resolveType(propSchema, propContext);

                if (!isRequired && !csharpType.EndsWith("?"))
                {
                    csharpType += "?";
                }

                var isDeprecated = propSchema.Deprecated;
                properties.Add(new RecordProperty(propName, csharpType, isRequired, isDeprecated));
            }
        }

        return SchemaClassifier.DeduplicateProperties(properties);
    }

    public GeneratedRecord MergeWithSiblingProperties(
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

    public GeneratedRecord MapRecord(string name, IOpenApiSchema schema)
    {
        if (!ctx.Resolving.Add(name))
        {
            return new GeneratedRecord(name, []);
        }

        var properties = ExtractProperties(schema, name);
        ctx.Resolving.Remove(name);
        return new GeneratedRecord(name, properties);
    }

    public GeneratedRecord? BuildGenericTemplateRecord(
        string templateName,
        GenericTemplateInfo info,
        IDictionary<string, IOpenApiSchema> schemas)
    {
        // Find the first monomorphised instance to derive the template properties
        IOpenApiSchema? firstInstance = null;
        foreach (var (key, schema) in schemas)
        {
            if (SchemaClassifier.TryGetGenericExtension(schema, out var schemaInfo) && schemaInfo!.Name == templateName)
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
            var templatedType = SchemaClassifier.ReverseSubstituteTypes(prop.CSharpType, reverseMap);
            templateProps.Add(prop with { CSharpType = templatedType });
        }

        return new GeneratedRecord(templateName, templateProps, info.TypeParams);
    }

    private string SanitizeName(string name)
    {
        if (ctx.SchemaNameMap.TryGetValue(name, out var mapped))
        {
            return mapped;
        }

        return Naming.ToPascalCaseFromSegments(name);
    }
}
