using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

internal static class TsTypeLeaf
{
    /// <summary>
    /// Overrides the schema type and format of a primitive leaf (or a nullable
    /// primitive). A null argument keeps the current value; an empty format clears it.
    /// Non-primitive types are returned unchanged: a member's <c>[RivetSchemaType]</c> is
    /// refused there first (<see cref="RequirePrimitiveLeaf"/>), and a builder call that pins
    /// <c>schemaType</c> on its param or content replaces the whole schema in the emitter.
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

    /// <summary>
    /// RIV1117: <c>[RivetSchemaType]</c> re-types a primitive leaf, so on a member whose type
    /// maps to anything else (an object, a collection, a brand) <see cref="WithLeaf"/> would
    /// drop it without a word. <paramref name="subject"/> names the member and its C# type.
    /// A builder call's <c>schemaType:</c> argument that re-types a leaf the same way
    /// (<c>.WithResponseHeader&lt;T&gt;(schemaType:)</c>) is held to the same rule.
    /// </summary>
    public static void RequirePrimitiveLeaf(
        this TsType type,
        string? schemaType,
        string subject,
        bool fromBuilderArgument = false
    )
    {
        if (
            schemaType is null
            || type is TsType.Primitive or TsType.Nullable { Inner: TsType.Primitive }
        )
        {
            return;
        }

        var (marker, kind, removal) = fromBuilderArgument
            ? ($"schemaType: \"{schemaType}\"", "argument", "drop schemaType:")
            : ($"[RivetSchemaType(\"{schemaType}\")]", "attribute", "remove [RivetSchemaType]");
        throw Diagnostics.Error(
            Diagnostics.SchemaTypeOnNonPrimitive,
            $"{marker} on {subject} has no primitive to re-type: "
                + $"the member maps to {Describe(type)}, not a primitive or a nullable primitive, so "
                + $"the {kind} would be dropped from the schema. If the type is a string (or another "
                + $"scalar) on the wire, mark it [RivetScalar] with a single Value property; otherwise "
                + $"{removal}."
        );
    }

    private static string Describe(TsType type) =>
        type switch
        {
            TsType.Nullable nullable => $"{Describe(nullable.Inner)} (nullable)",
            TsType.TypeRef reference => $"the schema '{reference.Name}'",
            TsType.Generic generic => $"the generic schema '{generic.Name}'",
            TsType.Brand brand => $"the [RivetScalar] brand '{brand.Name}'",
            TsType.TypeParam parameter => $"the type parameter '{parameter.Name}'",
            TsType.Array => "an array",
            TsType.Dictionary => "a dictionary",
            TsType.StringUnion or TsType.IntUnion => "an enum",
            TsType.Literal => "a literal",
            TsType.InlineObject => "an inline object",
            TsType.TaggedUnion or TsType.Union => "a union",
            _ => type.GetType().Name,
        };
}
