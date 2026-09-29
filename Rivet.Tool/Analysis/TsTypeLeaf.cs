using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

internal static class TsTypeLeaf
{
    /// <summary>
    /// Overrides the schema type and format of a primitive leaf (or a nullable
    /// primitive). A null argument keeps the current value; an empty format clears it.
    /// Non-primitive types are returned unchanged.
    /// </summary>
    public static TsType WithLeaf(this TsType type, string? schemaType, string? format)
    {
        TsType.Primitive Apply(TsType.Primitive primitive) =>
            primitive with
            {
                Name = schemaType ?? primitive.Name,
                Format =
                    format is null ? primitive.Format
                    : format == "" ? null
                    : format,
            };

        return type switch
        {
            TsType.Primitive primitive => Apply(primitive),
            TsType.Nullable { Inner: TsType.Primitive primitive } => new TsType.Nullable(
                Apply(primitive)
            ),
            _ => type,
        };
    }
}
