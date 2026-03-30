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
}
