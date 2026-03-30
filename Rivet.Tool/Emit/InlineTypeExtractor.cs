namespace Rivet.Tool.Emit;

using Rivet.Tool.Model;

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

    private static string ToPascalCase(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string Singularize(string name)
    {
        if (name.Length <= 3) return name;
        if (name.EndsWith("ies"))
            return name[..^3] + "y";
        if (name.EndsWith("s"))
            return name[..^1];
        return name;
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
