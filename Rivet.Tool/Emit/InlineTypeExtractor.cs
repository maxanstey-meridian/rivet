namespace Rivet.Tool.Emit;

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Rivet.Tool.Model;

public sealed record ExtractionResult(
    IReadOnlyList<TsEndpointDefinition> Endpoints,
    IReadOnlyList<TsTypeDefinition> ExtractedTypes
);

public static class InlineTypeExtractor
{
    private const int CrossControllerThreshold = 3;

    private static string Encode(string value) => $"{value.Length}:{value}";

    public static string CanonicalHash(TsType type)
    {
        return type switch
        {
            TsType.Primitive p =>
                $"P:{Encode(p.Name)}F:{Encode(p.Format ?? "")}C:{Encode(p.CSharpType ?? "")}",
            TsType.Nullable n => $"N:{CanonicalHash(n.Inner)}",
            TsType.Array a => $"A:{CanonicalHash(a.Element)}M:{MetadataHash(a.ElementMetadata)}",
            // Keyless dictionaries keep the historical hash so existing names stay stable
            TsType.Dictionary d => d.Key is null
                ? $"D:{CanonicalHash(d.Value)}M:{MetadataHash(d.ValueMetadata)}"
                : $"D[{CanonicalHash(d.Key)}]:{CanonicalHash(d.Value)}M:{MetadataHash(d.ValueMetadata)}",
            TsType.StringUnion su => "SU:"
                + string.Concat(su.Members.OrderBy(m => m).Select(Encode))
                + $"M:{Encode(JsonSerializer.Serialize(su with { Members = [] }))}",
            TsType.IntUnion iu => "IU:"
                + (iu.Format ?? "")
                + ":"
                + string.Join(",", iu.Members.OrderBy(m => m))
                + $"M:{Encode(JsonSerializer.Serialize(iu with { Members = [] }))}",
            TsType.Literal literal =>
                $"L:{literal.Value.ValueKind}:{Encode(literal.Value.ToString())}",
            TsType.TypeRef r => $"R:{Encode(r.Name)}",
            TsType.Generic g =>
                $"G:{Encode(g.Name)}<{string.Join(",", g.TypeArguments.Select(CanonicalHash))}>",
            TsType.TypeParam tp => $"TP:{Encode(tp.Name)}",
            TsType.Brand b => $"B:{Encode(b.Name)}({CanonicalHash(b.Inner)})",
            // An open or closed object is another shape than an unstated one.
            TsType.InlineObject obj => "IO:{"
                + string.Join(
                    ",",
                    obj.Properties.OrderBy(p => p.Name)
                        .Select(p => $"{AnnotationsHash(p)}T:{CanonicalHash(p.Type)}")
                )
                + "}"
                + obj.AdditionalProperties switch
                {
                    true => "+",
                    false => "-",
                    null => "",
                },
            TsType.TaggedUnion tu => "TU:"
                + Encode(tu.Discriminator)
                + "["
                + string.Join(
                    ",",
                    tu.Variants.OrderBy(v => v.Tag)
                        .Select(v => $"{AnnotationsHash(v)}T:{CanonicalHash(v.Type)}")
                )
                + "]",
            TsType.Union u => "U:["
                + string.Join(",", u.Variants.Select(CanonicalHash).OrderBy(h => h))
                + "]",
            _ => throw new NotSupportedException($"Unknown TsType variant: {type.GetType().Name}"),
        };
    }

    private static string MetadataHash(TsScalarMetadata? metadata) =>
        metadata is null ? "" : Encode(JsonSerializer.Serialize(metadata));

    // Every annotation a property or variant carries (its name or tag, optionality,
    // description, constraints, ...) tells two shapes apart: two variants that differ only in
    // a description are two component schemas, not one. Its type is left out, since
    // CanonicalHash hashes that on its own (an enum's members in any order, say), so the
    // serializer never writes that subtree here at all.
    private static readonly JsonSerializerOptions _annotationsOnly = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LeaveOutType } },
    };

    private static void LeaveOutType(JsonTypeInfo info)
    {
        if (
            info.Type == typeof(TsPropertyDefinition)
            || info.Type == typeof(TsType.TaggedUnionVariant)
        )
        {
            info.Properties.Remove(
                info.Properties.Single(property =>
                    property.Name == nameof(TsPropertyDefinition.Type)
                )
            );
        }
    }

    private static string AnnotationsHash<TRecord>(TRecord record) =>
        Encode(JsonSerializer.Serialize(record, _annotationsOnly));

    public static List<(TsType.InlineObject Type, string Context)> CollectInlineObjects(
        IReadOnlyList<TsEndpointDefinition> endpoints
    )
    {
        var results = new List<(TsType.InlineObject, string)>();
        foreach (var endpoint in endpoints)
        {
            foreach (var (site, type) in endpoint.AllTypes())
            {
                CollectFromType(type, $"{endpoint.ControllerName}.{endpoint.Name}.{site}", results);
            }
        }

        return results;
    }

    public static string GenerateName(
        string controllerName,
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences,
        HashSet<string> usedNames,
        Dictionary<string, TsType.InlineObject> nameTypes,
        TsType.InlineObject type,
        HashSet<string>? arrayElementHashes = null
    )
    {
        var baseName = DeriveBaseName(controllerName, occurrences);
        var suffix = IsResponseWrapper(occurrences, type) ? "Response" : "Dto";
        return GenerateName(baseName, suffix, usedNames, nameTypes, type, arrayElementHashes);
    }

    public static string GenerateName(
        string baseName,
        string suffix,
        HashSet<string> usedNames,
        Dictionary<string, TsType.InlineObject> nameTypes,
        TsType.InlineObject type,
        HashSet<string>? arrayElementHashes
    )
    {
        var name = baseName + suffix;
        if (usedNames.Contains(name))
        {
            name = DisambiguateCollision(name, usedNames, nameTypes, type, arrayElementHashes);
        }

        usedNames.Add(name);
        nameTypes[name] = type;
        return name;
    }

    private static string DeriveBaseName(
        string controllerName,
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences
    )
    {
        var nestedOccurrence = occurrences.FirstOrDefault(o => o.Context.Contains(".field."));
        if (nestedOccurrence != default)
        {
            var fieldName = nestedOccurrence.Context.Split(".field.").Last().Split('.').First();
            if (fieldName != "data")
            {
                return ToPascalCase(Singularize(fieldName));
            }
        }

        // Extract method name from context path: "Controller.Method.return" → segments[1]
        var segments = occurrences[0].Context.Split('.');
        var methodName = segments.Length > 1 ? segments[1] : "";
        return ToPascalCase(Singularize(controllerName)) + ToPascalCase(methodName);
    }

    private static readonly HashSet<string> _responseWrapperFields =
    [
        "data",
        "message",
        "error",
        "status",
    ];

    internal static bool IsResponseWrapper(
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences,
        TsType.InlineObject type
    )
    {
        // Condition 1: all occurrences are top-level (no .field. in context)
        // and context matches *.return or *.response.*
        var allTopLevel = occurrences.All(o =>
        {
            var ctx = o.Context;
            if (ctx.Contains(".field."))
            {
                return false;
            }

            return ctx.EndsWith(".return") || ctx.Contains(".response.");
        });
        if (!allTopLevel)
        {
            return false;
        }

        // Condition 2: type has at least one wrapper field
        return type.Properties.Any(f => _responseWrapperFields.Contains(f.Name));
    }

    internal static string DisambiguateCollision(
        string name,
        HashSet<string> usedNames,
        Dictionary<string, TsType.InlineObject> nameTypes,
        TsType.InlineObject type,
        HashSet<string>? arrayElementHashes = null
    )
    {
        // Strip known suffix to get baseName and suffix
        string baseName,
            suffix;
        if (name.EndsWith("Response"))
        {
            baseName = name[..^8];
            suffix = "Response";
        }
        else if (name.EndsWith("Dto"))
        {
            baseName = name[..^3];
            suffix = "Dto";
        }
        else
        {
            baseName = name;
            suffix = "";
        }

        // Strategy 1: Array element → Ref
        if (arrayElementHashes is not null && arrayElementHashes.Contains(CanonicalHash(type)))
        {
            var candidate = baseName + "Ref" + suffix;
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        if (nameTypes.TryGetValue(name, out var existing))
        {
            if (type.Properties.Count < existing.Properties.Count)
            {
                var candidate = baseName + "Summary" + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                {
                    return candidate;
                }
            }

            if (type.Properties.Count > existing.Properties.Count)
            {
                var candidate = baseName + "Detail" + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                {
                    return candidate;
                }
            }

            // Strategy: same field names but different optionality → Ref for the more-optional variant
            if (type.Properties.Count == existing.Properties.Count)
            {
                var typeOptional = type.Properties.Count(f => f.IsOptional);
                var existingOptional = existing.Properties.Count(f => f.IsOptional);
                if (typeOptional > existingOptional)
                {
                    var candidate = baseName + "Ref" + suffix;
                    if (!usedNames.Contains(candidate))
                    {
                        return candidate;
                    }
                }
                else if (typeOptional < existingOptional)
                {
                    var candidate = baseName + "Detail" + suffix;
                    if (!usedNames.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }

            // Find a distinguishing field name
            var existingFieldNames = existing.Properties.Select(f => f.Name).ToHashSet();
            var distinguishing = type.Properties.FirstOrDefault(f =>
                !existingFieldNames.Contains(f.Name)
            );
            if (distinguishing != default)
            {
                var candidate = baseName + ToPascalCase(distinguishing.Name) + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                {
                    return candidate;
                }
            }
        }

        return Naming.NameCandidates(name).First(candidate => !usedNames.Contains(candidate));
    }

    private static bool HasStutter(string name)
    {
        var words = SplitPascalCase(name);
        for (var i = 1; i < words.Count; i++)
        {
            if (words[i].Equals(words[i - 1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SplitPascalCase(string s)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 1; i < s.Length; i++)
        {
            if (char.IsUpper(s[i]))
            {
                words.Add(s[start..i]);
                start = i;
            }
        }
        words.Add(s[start..]);
        return words;
    }

    private static readonly HashSet<string> _commonFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "created_at",
        "updated_at",
    };

    internal static string DeriveStructuralName(TsType.InlineObject type)
    {
        var fields = type.Properties.Where(f => f.Name != "data").ToList();
        // Don't strip data when it's the only field — "Object" is never useful
        if (fields.Count == 0)
        {
            fields = type.Properties.ToList();
        }

        if (fields.Count == 0)
        {
            return "Object";
        }

        // Prefer distinctive fields over common boilerplate
        var distinctive = fields.Where(f => !_commonFields.Contains(f.Name)).ToList();
        var naming = distinctive.Count > 0 ? distinctive : fields;

        if (naming.Count <= 2)
        {
            return string.Concat(naming.Select(f => ToPascalCase(f.Name)));
        }
        // Cap at 2 fields — no Plus{N} suffix
        return string.Concat(naming.Take(2).Select(f => ToPascalCase(f.Name)));
    }

    internal static string ToPascalCase(string s) =>
        string.Concat(
            s.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(seg => char.ToUpperInvariant(seg[0]) + seg[1..])
        );

    public static string Singularize(string name)
    {
        if (name.Length <= 3)
        {
            return name;
        }

        if (name.EndsWith("ies"))
        {
            return name[..^3] + "y";
        }

        if (name.EndsWith("s"))
        {
            return name[..^1];
        }

        return name;
    }

    public static ExtractionResult Extract(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyList<TsTypeDefinition> existingDefinitions,
        int fieldThreshold = 5
    )
    {
        var collected = CollectInlineObjects(endpoints);

        // Group by canonical hash
        var groups = collected.GroupBy(c => CanonicalHash(c.Type)).ToList();

        var usedNames = new HashSet<string>(existingDefinitions.Select(d => d.Name));
        var nameTypes = new Dictionary<string, TsType.InlineObject>();
        var arrayElementHashes = CollectArrayElementHashes(endpoints);
        var replacements = new Dictionary<string, TsType.TypeRef>();
        var extractedTypes = new List<TsTypeDefinition>();

        foreach (var group in groups)
        {
            var items = group.ToList();
            var representative = items[0].Type;

            if (items.Count < 2 && representative.Properties.Count < fieldThreshold)
            {
                continue;
            }

            var distinctControllers = items.Select(i => i.Context.Split('.')[0]).Distinct().Count();
            var suffix = IsResponseWrapper(items, representative) ? "Response" : "Dto";
            string name;
            if (distinctControllers >= CrossControllerThreshold)
            {
                var structuralBase = DeriveStructuralName(representative);
                name = GenerateName(
                    structuralBase,
                    suffix,
                    usedNames,
                    nameTypes,
                    representative,
                    arrayElementHashes
                );
            }
            else
            {
                var controllerName = items[0].Context.Split('.')[0];
                name = GenerateName(
                    controllerName,
                    items,
                    usedNames,
                    nameTypes,
                    representative,
                    arrayElementHashes
                );
            }

            replacements[group.Key] = new TsType.TypeRef(name);
        }

        // Build type definitions with replaced field types
        foreach (var (hash, typeRef) in replacements)
        {
            var representative = collected.First(c => CanonicalHash(c.Type) == hash).Type;
            var properties = representative
                .Properties.Select(p => p with { Type = ReplaceInType(p.Type, replacements) })
                .ToList();

            extractedTypes.Add(
                new TsTypeDefinition(
                    typeRef.Name,
                    [],
                    properties,
                    AdditionalProperties: representative.AdditionalProperties
                )
            );
        }

        // Replace InlineObjects in all endpoints
        var updatedEndpoints = endpoints.Select(e => ReplaceInEndpoint(e, replacements)).ToList();

        return new ExtractionResult(updatedEndpoints, extractedTypes);
    }

    private static TsEndpointDefinition ReplaceInEndpoint(
        TsEndpointDefinition endpoint,
        Dictionary<string, TsType.TypeRef> replacements
    )
    {
        TsType Replace(TsType type) => ReplaceInType(type, replacements);

        return endpoint with
        {
            ReturnType = endpoint.ReturnType is null ? null : Replace(endpoint.ReturnType),
            Responses = endpoint
                .Responses.Select(response =>
                    response with
                    {
                        DataType = response.DataType is null ? null : Replace(response.DataType),
                        Contents = response
                            .Contents?.Select(content =>
                                content with
                                {
                                    Schema = content.Schema is null
                                        ? null
                                        : Replace(content.Schema),
                                }
                            )
                            .ToList(),
                        Headers = response
                            .Headers?.Select(header => header with { Type = Replace(header.Type) })
                            .ToList(),
                    }
                )
                .ToList(),
            Params = endpoint.Params.Select(p => p with { Type = Replace(p.Type) }).ToList(),
            RequestType = endpoint.RequestType is null ? null : Replace(endpoint.RequestType),
            RequestContents = endpoint
                .RequestContents?.Select(content =>
                    content with
                    {
                        Schema = content.Schema is null ? null : Replace(content.Schema),
                    }
                )
                .ToList(),
        };
    }

    private static TsType ReplaceInType(
        TsType type,
        Dictionary<string, TsType.TypeRef> replacements
    ) =>
        type.Rewrite(node =>
            node switch
            {
                TsType.InlineObject inline
                    when replacements.TryGetValue(CanonicalHash(inline), out var typeRef) =>
                    typeRef,
                TsType.TaggedUnion union => union with
                {
                    Variants = union
                        .Variants.Select(variant =>
                            variant with
                            {
                                Type = variant.Type is TsType.InlineObject own
                                    ? own with
                                    {
                                        Properties = own
                                            .Properties.Select(field =>
                                                field with
                                                {
                                                    Type = ReplaceInType(field.Type, replacements),
                                                }
                                            )
                                            .ToList(),
                                    }
                                    : ReplaceInType(variant.Type, replacements),
                            }
                        )
                        .ToList(),
                },
                _ => null,
            }
        );

    private static HashSet<string> CollectArrayElementHashes(
        IReadOnlyList<TsEndpointDefinition> endpoints
    ) =>
        endpoints
            .SelectMany(endpoint => endpoint.AllTypes())
            .SelectMany(site => site.Type.SelfAndDescendants())
            .OfType<TsType.Array>()
            .Select(array => array.Element)
            .OfType<TsType.InlineObject>()
            .Select(CanonicalHash)
            .ToHashSet();

    private static void CollectFromType(
        TsType type,
        string context,
        List<(TsType.InlineObject, string)> results
    )
    {
        switch (type)
        {
            case TsType.InlineObject io:
                results.Add((io, context));
                foreach (var field in io.Properties)
                {
                    CollectFromType(field.Type, $"{context}.field.{field.Name}", results);
                }

                break;
            // A variant's own inline object is never extracted: it already becomes its
            // {Base}_{Tag} component, and that component carries the variant's description,
            // scalar metadata and authored tag schema, which a Dto reference would cut off.
            // ReplaceInType keeps it inline for the same reason. What sits inside it is fair game.
            case TsType.TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    var variantContext = $"{context}.variant.{variant.Tag}";
                    if (variant.Type is TsType.InlineObject own)
                    {
                        foreach (var field in own.Properties)
                        {
                            CollectFromType(
                                field.Type,
                                $"{variantContext}.field.{field.Name}",
                                results
                            );
                        }
                    }
                    else
                    {
                        CollectFromType(variant.Type, variantContext, results);
                    }
                }

                break;
            case TsType.Union u:
                for (var index = 0; index < u.Variants.Count; index++)
                {
                    CollectFromType(u.Variants[index], $"{context}.variant{index}", results);
                }

                break;
            default:
                foreach (var child in type.Children())
                {
                    CollectFromType(child, context, results);
                }

                break;
        }
    }
}
