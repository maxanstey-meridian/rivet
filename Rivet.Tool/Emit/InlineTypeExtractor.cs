namespace Rivet.Tool.Emit;

using Rivet.Tool.Model;

public sealed record ExtractionResult(
    IReadOnlyList<TsEndpointDefinition> Endpoints,
    IReadOnlyList<TsTypeDefinition> ExtractedTypes,
    IReadOnlyDictionary<string, string?> TypeNamespaces);

public static class InlineTypeExtractor
{
    private const int CrossControllerThreshold = 3;

    public static string CanonicalHash(TsType type)
    {
        return type switch
        {
            TsType.Primitive p => p.Format is not null ? $"P:{p.Name}:{p.Format}" : $"P:{p.Name}",
            TsType.Nullable n => $"N:{CanonicalHash(n.Inner)}",
            TsType.Array a => $"A:{CanonicalHash(a.Element)}",
            TsType.Dictionary d => $"D:{CanonicalHash(d.Value)}",
            TsType.StringUnion su => "SU:" + string.Join(",", su.Members.OrderBy(m => m)),
            TsType.IntUnion iu => "IU:" + string.Join(",", iu.Members.OrderBy(m => m)),
            TsType.TypeRef r => $"R:{r.Name}",
            TsType.Generic g => $"G:{g.Name}<{string.Join(",", g.TypeArguments.Select(CanonicalHash))}>",
            TsType.TypeParam tp => $"TP:{tp.Name}",
            TsType.Brand b => $"B:{b.Name}({CanonicalHash(b.Inner)})",
            TsType.InlineObject obj => "IO:{" + string.Join(",",
                obj.Fields.OrderBy(f => f.Name).Select(f => $"{f.Name}:{CanonicalHash(f.Type)}")) + "}",
            _ => throw new NotSupportedException($"Unknown TsType variant: {type.GetType().Name}"),
        };
    }

    public static List<(TsType.InlineObject Type, string Context)> CollectInlineObjects(
        IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var results = new List<(TsType.InlineObject, string)>();

        foreach (var e in endpoints)
        {
            CollectFromType(e.ReturnType, $"{e.ControllerName}.{e.Name}.return", results);

            foreach (var r in e.Responses)
                CollectFromType(r.DataType, $"{e.ControllerName}.{e.Name}.response.{r.StatusCode}", results);

            foreach (var p in e.Params)
                CollectFromType(p.Type, $"{e.ControllerName}.{e.Name}.param.{p.Name}", results);

            if (e.RequestType is not null)
                CollectFromType(e.RequestType, $"{e.ControllerName}.{e.Name}.requestType", results);
        }

        return results;
    }

    public static string GenerateName(
        string controllerName,
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences,
        HashSet<string> usedNames)
    {
        var baseName = DeriveBaseName(controllerName, occurrences);
        var suffix = IsResponseWrapper(occurrences, occurrences[0].Type) ? "Response" : "Dto";
        return GenerateName(baseName, suffix, usedNames);
    }

    public static string GenerateName(
        string controllerName,
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences,
        HashSet<string> usedNames,
        Dictionary<string, TsType.InlineObject> nameTypes,
        TsType.InlineObject type,
        HashSet<string>? arrayElementHashes = null)
    {
        var baseName = DeriveBaseName(controllerName, occurrences);
        var suffix = IsResponseWrapper(occurrences, type) ? "Response" : "Dto";
        return GenerateName(baseName, suffix, usedNames, nameTypes, type, arrayElementHashes);
    }

    public static string GenerateName(string baseName, HashSet<string> usedNames)
    {
        return GenerateName(baseName, "Dto", usedNames);
    }

    public static string GenerateName(string baseName, string suffix, HashSet<string> usedNames)
    {
        var name = baseName + suffix;
        name = ResolveCollision(name, usedNames);
        usedNames.Add(name);
        return name;
    }

    public static string GenerateName(
        string baseName,
        string suffix,
        HashSet<string> usedNames,
        Dictionary<string, TsType.InlineObject> nameTypes,
        TsType.InlineObject type,
        HashSet<string>? arrayElementHashes)
    {
        var name = baseName + suffix;
        if (usedNames.Contains(name))
            name = DisambiguateCollision(name, usedNames, nameTypes, type, arrayElementHashes);
        usedNames.Add(name);
        nameTypes[name] = type;
        return name;
    }

    private static string DeriveBaseName(
        string controllerName,
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences)
    {
        var nestedOccurrence = occurrences.FirstOrDefault(o => o.Context.Contains(".field."));
        if (nestedOccurrence != default)
        {
            var fieldName = nestedOccurrence.Context.Split(".field.").Last().Split('.').First();
            if (fieldName != "data")
                return ToPascalCase(Singularize(fieldName));
        }

        // Extract method name from context path: "Controller.Method.return" → segments[1]
        var segments = occurrences[0].Context.Split('.');
        var methodName = segments.Length > 1 ? segments[1] : "";
        return ToPascalCase(Singularize(controllerName)) + ToPascalCase(methodName);
    }

    private static readonly HashSet<string> ResponseWrapperFields = ["data", "message", "error", "status"];

    internal static bool IsResponseWrapper(
        IReadOnlyList<(TsType.InlineObject Type, string Context)> occurrences,
        TsType.InlineObject type)
    {
        // Condition 1: all occurrences are top-level (no .field. in context)
        // and context matches *.return or *.response.*
        var allTopLevel = occurrences.All(o =>
        {
            var ctx = o.Context;
            if (ctx.Contains(".field.")) return false;
            return ctx.EndsWith(".return") || ctx.Contains(".response.");
        });
        if (!allTopLevel) return false;

        // Condition 2: type has at least one wrapper field
        return type.Fields.Any(f => ResponseWrapperFields.Contains(f.Name));
    }

    internal static string DisambiguateCollision(
        string name,
        HashSet<string> usedNames,
        Dictionary<string, TsType.InlineObject> nameTypes,
        TsType.InlineObject type,
        HashSet<string>? arrayElementHashes = null)
    {
        // Strip known suffix to get baseName and suffix
        string baseName, suffix;
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
                return candidate;
        }

        if (nameTypes.TryGetValue(name, out var existing))
        {
            if (type.Fields.Count < existing.Fields.Count)
            {
                var candidate = baseName + "Summary" + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                    return candidate;
            }

            if (type.Fields.Count > existing.Fields.Count)
            {
                var candidate = baseName + "Detail" + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                    return candidate;
            }

            // Strategy: same field names but different optionality → Ref for the more-optional variant
            if (type.Fields.Count == existing.Fields.Count)
            {
                var typeOptional = type.Fields.Count(f => f.Type is TsType.Nullable);
                var existingOptional = existing.Fields.Count(f => f.Type is TsType.Nullable);
                if (typeOptional > existingOptional)
                {
                    var candidate = baseName + "Ref" + suffix;
                    if (!usedNames.Contains(candidate))
                        return candidate;
                }
                else if (typeOptional < existingOptional)
                {
                    var candidate = baseName + "Detail" + suffix;
                    if (!usedNames.Contains(candidate))
                        return candidate;
                }
            }

            // Find a distinguishing field name
            var existingFieldNames = existing.Fields.Select(f => f.Name).ToHashSet();
            var distinguishing = type.Fields.FirstOrDefault(f => !existingFieldNames.Contains(f.Name));
            if (distinguishing != default)
            {
                var candidate = baseName + ToPascalCase(distinguishing.Name) + suffix;
                if (!usedNames.Contains(candidate) && !HasStutter(candidate))
                    return candidate;
            }
        }

        return ResolveCollision(name, usedNames);
    }

    private static bool HasStutter(string name)
    {
        var words = SplitPascalCase(name);
        for (var i = 1; i < words.Count; i++)
            if (words[i].Equals(words[i - 1], StringComparison.OrdinalIgnoreCase))
                return true;
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
        if (!usedNames.Contains(name)) return name;
        var i = 2;
        while (usedNames.Contains(name + i)) i++;
        return name + i;
    }

    private static readonly HashSet<string> CommonFields =
        new(StringComparer.OrdinalIgnoreCase) { "id", "created_at", "updated_at" };

    internal static string DeriveStructuralName(TsType.InlineObject type)
    {
        var fields = type.Fields.Where(f => f.Name != "data").ToList();
        // Don't strip data when it's the only field — "Object" is never useful
        if (fields.Count == 0)
            fields = type.Fields.ToList();
        if (fields.Count == 0)
            return "Object";

        // Prefer distinctive fields over common boilerplate
        var distinctive = fields.Where(f => !CommonFields.Contains(f.Name)).ToList();
        var naming = distinctive.Count > 0 ? distinctive : fields;

        if (naming.Count <= 2)
            return string.Concat(naming.Select(f => ToPascalCase(f.Name)));
        // Cap at 2 fields — no Plus{N} suffix
        return string.Concat(naming.Take(2).Select(f => ToPascalCase(f.Name)));
    }

    internal static string ToPascalCase(string s) =>
        string.Concat(s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => char.ToUpperInvariant(seg[0]) + seg[1..]));

    public static string Singularize(string name)
    {
        if (name.Length <= 3) return name;
        if (name.EndsWith("ies"))
            return name[..^3] + "y";
        if (name.EndsWith("s"))
            return name[..^1];
        return name;
    }

    public static ExtractionResult Extract(
        IReadOnlyList<TsEndpointDefinition> endpoints,
        IReadOnlyList<TsTypeDefinition> existingDefinitions,
        int fieldThreshold = 5)
    {
        var collected = CollectInlineObjects(endpoints);

        // Group by canonical hash
        var groups = collected
            .GroupBy(c => CanonicalHash(c.Type))
            .ToList();

        var usedNames = new HashSet<string>(existingDefinitions.Select(d => d.Name));
        var nameTypes = new Dictionary<string, TsType.InlineObject>();
        var arrayElementHashes = CollectArrayElementHashes(endpoints);
        var replacements = new Dictionary<string, TsType.TypeRef>();
        var extractedTypes = new List<TsTypeDefinition>();
        var typeNamespaces = new Dictionary<string, string?>();

        foreach (var group in groups)
        {
            var items = group.ToList();
            var representative = items[0].Type;

            if (items.Count < 2 && representative.Fields.Count < fieldThreshold)
                continue;

            var distinctControllers = items.Select(i => i.Context.Split('.')[0]).Distinct().Count();
            var suffix = IsResponseWrapper(items, representative) ? "Response" : "Dto";
            string name;
            if (distinctControllers >= CrossControllerThreshold)
            {
                var structuralBase = DeriveStructuralName(representative);
                name = GenerateName(structuralBase, suffix, usedNames, nameTypes, representative, arrayElementHashes);
            }
            else
            {
                var controllerName = items[0].Context.Split('.')[0];
                name = GenerateName(controllerName, items, usedNames, nameTypes, representative, arrayElementHashes);
            }

            replacements[group.Key] = new TsType.TypeRef(name);
            typeNamespaces[name] = null;
        }

        // Build type definitions with replaced field types
        foreach (var (hash, typeRef) in replacements)
        {
            var representative = collected.First(c => CanonicalHash(c.Type) == hash).Type;
            var properties = representative.Fields
                .Select(f => BuildPropertyDefinition(f.Name, ReplaceInType(f.Type, replacements)))
                .ToList();

            extractedTypes.Add(new TsTypeDefinition(typeRef.Name, [], properties));
        }

        // Replace InlineObjects in all endpoints
        var updatedEndpoints = endpoints.Select(e => ReplaceInEndpoint(e, replacements)).ToList();

        return new ExtractionResult(updatedEndpoints, extractedTypes, typeNamespaces);
    }

    private static TsPropertyDefinition BuildPropertyDefinition(string name, TsType type)
    {
        if (type is TsType.Nullable nullable)
            return new TsPropertyDefinition(name, nullable.Inner, IsOptional: true);

        return new TsPropertyDefinition(name, type, IsOptional: false);
    }

    private static TsEndpointDefinition ReplaceInEndpoint(
        TsEndpointDefinition endpoint,
        Dictionary<string, TsType.TypeRef> replacements)
    {
        var returnType = endpoint.ReturnType is not null
            ? ReplaceInType(endpoint.ReturnType, replacements)
            : null;

        var responses = endpoint.Responses
            .Select(r => r.DataType is not null
                ? r with { DataType = ReplaceInType(r.DataType, replacements) }
                : r)
            .ToList();

        var parameters = endpoint.Params
            .Select(p => p with { Type = ReplaceInType(p.Type, replacements) })
            .ToList();

        var requestType = endpoint.RequestType is not null
            ? ReplaceInType(endpoint.RequestType, replacements)
            : null;

        return endpoint with
        {
            ReturnType = returnType,
            Responses = responses,
            Params = parameters,
            RequestType = requestType,
        };
    }

    private static TsType ReplaceInType(TsType type, Dictionary<string, TsType.TypeRef> replacements)
    {
        switch (type)
        {
            case TsType.InlineObject io:
                var hash = CanonicalHash(io);
                if (replacements.TryGetValue(hash, out var typeRef))
                    return typeRef;
                var replacedFields = io.Fields
                    .Select(f => (f.Name, Type: ReplaceInType(f.Type, replacements)))
                    .ToList();
                return new TsType.InlineObject(replacedFields);

            case TsType.Array a:
                return new TsType.Array(ReplaceInType(a.Element, replacements));

            case TsType.Nullable n:
                return new TsType.Nullable(ReplaceInType(n.Inner, replacements));

            case TsType.Dictionary d:
                return new TsType.Dictionary(ReplaceInType(d.Value, replacements));

            case TsType.Generic g:
                var replacedArgs = g.TypeArguments.Select(a => ReplaceInType(a, replacements)).ToList();
                return new TsType.Generic(g.Name, replacedArgs);

            case TsType.Brand b:
                return new TsType.Brand(b.Name, ReplaceInType(b.Inner, replacements));

            default:
                return type;
        }
    }

    private static HashSet<string> CollectArrayElementHashes(IReadOnlyList<TsEndpointDefinition> endpoints)
    {
        var hashes = new HashSet<string>();
        foreach (var e in endpoints)
        {
            CollectArrayElements(e.ReturnType, hashes);
            foreach (var r in e.Responses)
                CollectArrayElements(r.DataType, hashes);
            foreach (var p in e.Params)
                CollectArrayElements(p.Type, hashes);
            if (e.RequestType is not null)
                CollectArrayElements(e.RequestType, hashes);
        }
        return hashes;
    }

    private static void CollectArrayElements(TsType? type, HashSet<string> hashes)
    {
        switch (type)
        {
            case TsType.Array a:
                if (a.Element is TsType.InlineObject io)
                    hashes.Add(CanonicalHash(io));
                CollectArrayElements(a.Element, hashes);
                break;
            case TsType.InlineObject obj:
                foreach (var (_, fieldType) in obj.Fields)
                    CollectArrayElements(fieldType, hashes);
                break;
            case TsType.Nullable n:
                CollectArrayElements(n.Inner, hashes);
                break;
            case TsType.Dictionary d:
                CollectArrayElements(d.Value, hashes);
                break;
            case TsType.Generic g:
                foreach (var arg in g.TypeArguments)
                    CollectArrayElements(arg, hashes);
                break;
            case TsType.Brand b:
                CollectArrayElements(b.Inner, hashes);
                break;
        }
    }

    private static void CollectFromType(TsType? type, string context,
        List<(TsType.InlineObject, string)> results)
    {
        switch (type)
        {
            case TsType.InlineObject io:
                results.Add((io, context));
                foreach (var (fieldName, fieldType) in io.Fields)
                    CollectFromType(fieldType, $"{context}.field.{fieldName}", results);
                break;
            case TsType.Array a:
                CollectFromType(a.Element, context, results);
                break;
            case TsType.Nullable n:
                CollectFromType(n.Inner, context, results);
                break;
            case TsType.Dictionary d:
                CollectFromType(d.Value, context, results);
                break;
            case TsType.Generic g:
                foreach (var arg in g.TypeArguments)
                    CollectFromType(arg, context, results);
                break;
            case TsType.Brand b:
                CollectFromType(b.Inner, context, results);
                break;
        }
    }
}
