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

    private static void CollectFromType(TsType? type, string context,
        List<(TsType.InlineObject, string)> results)
    {
        switch (type)
        {
            case TsType.InlineObject io:
                results.Add((io, context));
                foreach (var (_, fieldType) in io.Fields)
                    CollectFromType(fieldType, context, results);
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
