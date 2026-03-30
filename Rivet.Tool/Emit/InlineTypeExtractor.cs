namespace Rivet.Tool.Emit;

using Rivet.Tool.Model;

public sealed record ExtractionResult(
    IReadOnlyList<TsEndpointDefinition> Endpoints,
    IReadOnlyList<TsTypeDefinition> ExtractedTypes,
    IReadOnlyDictionary<string, string?> TypeNamespaces);

public static class InlineTypeExtractor
{
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
        var name = DeriveBaseName(controllerName, occurrences) + "Dto";
        name = ResolveCollision(name, usedNames);
        usedNames.Add(name);
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
            return ToPascalCase(Singularize(fieldName));
        }

        return Singularize(controllerName);
    }

    private static string ResolveCollision(string name, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(name)) return name;
        var i = 2;
        while (usedNames.Contains(name + i)) i++;
        return name + i;
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
        var replacements = new Dictionary<string, TsType.TypeRef>();
        var extractedTypes = new List<TsTypeDefinition>();
        var typeNamespaces = new Dictionary<string, string?>();

        foreach (var group in groups)
        {
            var items = group.ToList();
            var representative = items[0].Type;

            if (items.Count < 2 && representative.Fields.Count < fieldThreshold)
                continue;

            // Derive controller name from first occurrence context
            var controllerName = items[0].Context.Split('.')[0];
            var name = GenerateName(controllerName, items, usedNames);

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
