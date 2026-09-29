namespace Rivet.Tool.Emit;

using System.Text.Json;
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
                + string.Concat(su.Members.OrderBy(m => m).Select(Encode)),
            TsType.IntUnion iu => "IU:"
                + (iu.Format ?? "")
                + ":"
                + string.Join(",", iu.Members.OrderBy(m => m)),
            TsType.Literal literal =>
                $"L:{literal.Value.ValueKind}:{Encode(literal.Value.ToString())}",
            TsType.TypeRef r => $"R:{Encode(r.Name)}",
            TsType.Generic g =>
                $"G:{Encode(g.Name)}<{string.Join(",", g.TypeArguments.Select(CanonicalHash))}>",
            TsType.TypeParam tp => $"TP:{Encode(tp.Name)}",
            TsType.Brand b => $"B:{Encode(b.Name)}({CanonicalHash(b.Inner)})",
            TsType.InlineObject obj => "IO:{"
                + string.Join(
                    ",",
                    obj.Fields.OrderBy(f => f.Name)
                        .Select(f =>
                            $"N:{Encode(f.Name)}O:{(f.Optional ? 1 : 0)}S:{(int)f.Surface}"
                            + $"T:{CanonicalHash(f.Type)}"
                        )
                )
                + "}",
            TsType.TaggedUnion tu => "TU:"
                + Encode(tu.Discriminator)
                + "["
                + string.Join(
                    ",",
                    tu.Variants.OrderBy(v => v.Tag)
                        .Select(v => $"{Encode(v.Tag)}:{CanonicalHash(v.Type)}")
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
        return type.Fields.Any(f => _responseWrapperFields.Contains(f.Name));
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
            if (type.Fields.Count < existing.Fields.Count)
            {
                var candidate = baseName + "Summary" + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                {
                    return candidate;
                }
            }

            if (type.Fields.Count > existing.Fields.Count)
            {
                var candidate = baseName + "Detail" + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                {
                    return candidate;
                }
            }

            // Strategy: same field names but different optionality → Ref for the more-optional variant
            if (type.Fields.Count == existing.Fields.Count)
            {
                var typeOptional = type.Fields.Count(f => f.Optional);
                var existingOptional = existing.Fields.Count(f => f.Optional);
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
            var existingFieldNames = existing.Fields.Select(f => f.Name).ToHashSet();
            var distinguishing = type.Fields.FirstOrDefault(f =>
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

        return ResolveCollision(name, usedNames);
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

    private static string ResolveCollision(string name, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(name))
        {
            return name;
        }

        var i = 2;
        while (usedNames.Contains(name + i))
        {
            i++;
        }

        return name + i;
    }

    private static readonly HashSet<string> _commonFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "created_at",
        "updated_at",
    };

    internal static string DeriveStructuralName(TsType.InlineObject type)
    {
        var fields = type.Fields.Where(f => f.Name != "data").ToList();
        // Don't strip data when it's the only field — "Object" is never useful
        if (fields.Count == 0)
        {
            fields = type.Fields.ToList();
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

            if (items.Count < 2 && representative.Fields.Count < fieldThreshold)
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
                .Fields.Select(f => new TsPropertyDefinition(
                    f.Name,
                    ReplaceInType(f.Type, replacements),
                    IsOptional: f.Optional,
                    IsReadOnly: f.Surface == TsType.InlineObjectFieldSurface.ResponseOnly,
                    IsWriteOnly: f.Surface == TsType.InlineObjectFieldSurface.RequestOnly
                ))
                .ToList();

            extractedTypes.Add(new TsTypeDefinition(typeRef.Name, [], properties));
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
            node is TsType.InlineObject inline
            && replacements.TryGetValue(CanonicalHash(inline), out var typeRef)
                ? typeRef
                : null
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
                foreach (var field in io.Fields)
                {
                    CollectFromType(field.Type, $"{context}.field.{field.Name}", results);
                }

                break;
            case TsType.TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    CollectFromType(variant.Type, $"{context}.variant.{variant.Tag}", results);
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
