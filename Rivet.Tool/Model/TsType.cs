using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

/// <summary>
/// Intermediate representation of a TypeScript type expression.
/// Produced by the type walker, consumed by the emitter.
/// </summary>
[JsonConverter(typeof(TsTypeJsonConverter))]
public abstract record TsType
{
    private TsType() { }

    /// <summary>Leaf type: "string", "number", "boolean", "unknown". Optional Format for OpenAPI/JSON Schema.
    /// CSharpType is set when the C# type can't be recovered from Name+Format alone (e.g. DateTimeOffset, uint).</summary>
    public sealed record Primitive(string Name, string? Format = null, string? CSharpType = null)
        : TsType;

    /// <summary>T | null.</summary>
    public sealed record Nullable(TsType Inner) : TsType;

    /// <summary>T[].</summary>
    public sealed record Array(TsType Element, TsScalarMetadata? ElementMetadata = null) : TsType;

    /// <summary>Record&lt;string, T&gt;. Key is null for plain string keys; otherwise the
    /// key's contract representation (enum ref, string-backed brand, or a string-typed
    /// primitive carrying the original format/CSharpType) — emitted as propertyNames.</summary>
    public sealed record Dictionary(
        TsType Value,
        TsType? Key = null,
        TsScalarMetadata? ValueMetadata = null
    ) : TsType;

    /// <summary>"A" | "B" | "C" — string enum rendered as union.</summary>
    public sealed record StringUnion(
        IReadOnlyList<string> Members,
        TsTypeMetadata? Metadata = null,
        string? Format = null,
        string? Description = null,
        TsScalarMetadata? ScalarMetadata = null
    ) : TsType;

    /// <summary>1 | 2 | 3 — int enum rendered as numeric literal union.</summary>
    public sealed record IntUnion(
        IReadOnlyList<int> Members,
        string? Format = null,
        TsTypeMetadata? Metadata = null,
        string? Description = null,
        TsScalarMetadata? ScalarMetadata = null
    ) : TsType;

    /// <summary>A JSON scalar literal type represented with OpenAPI 3.1 const.</summary>
    public sealed record Literal(JsonElement Value) : TsType;

    /// <summary>Reference to another emitted type by name.</summary>
    public sealed record TypeRef(string Name) : TsType;

    /// <summary>Generic type application: Foo&lt;T, U&gt;.</summary>
    public sealed record Generic(string Name, IReadOnlyList<TsType> TypeArguments) : TsType;

    /// <summary>Unresolved generic type parameter: T, TKey, etc.</summary>
    public sealed record TypeParam(string Name) : TsType;

    /// <summary>Branded primitive: string &amp; { readonly __brand: "Email" }.</summary>
    public sealed record Brand(
        string Name,
        TsType Inner,
        TsTypeMetadata? Metadata = null,
        string? Description = null
    ) : TsType;

    /// <summary>Inline object: { key: string; value?: number }. Used for tuples and union variants.</summary>
    public sealed record InlineObject(IReadOnlyList<InlineObjectField> Fields) : TsType;

    public sealed record InlineObjectField(string Name, TsType Type, bool Optional = false)
    {
        // Keep concise tuple syntax without conflating a nullable value with an absent field.
        public static implicit operator InlineObjectField((string Name, TsType Type) field) =>
            new(field.Name, field.Type, Optional: false);
    }

    /// <summary>Discriminated union of object-like variants keyed by a shared string-literal field.</summary>
    public sealed record TaggedUnion(
        string Discriminator,
        IReadOnlyList<TaggedUnionVariant> Variants
    ) : TsType;

    /// <summary>An undiscriminated union (oneOf without discriminator) — [RivetUnion] wrappers.</summary>
    public sealed record Union(IReadOnlyList<TsType> Variants) : TsType;

    public sealed record TaggedUnionVariant(
        string Tag,
        TsType Type,
        TsTypeMetadata? Metadata = null
    );

    /// <summary>
    /// Produces a stable, human-readable name suffix for a TsType.
    /// Used by emitters to generate unique names for monomorphised generics, validators, etc.
    /// </summary>
    public static string GetNameSuffix(TsType type)
    {
        return type switch
        {
            TypeRef r => r.Name,
            TypeParam tp => tp.Name,
            Primitive p => char.ToUpperInvariant(p.Name[0]) + p.Name[1..],
            Generic g => MonomorphisedName(g),
            Array a => GetNameSuffix(a.Element) + "Array",
            Nullable n => GetNameSuffix(n.Inner) + "Nullable",
            Brand b => b.Name,
            Dictionary d => "Record" + GetNameSuffix(d.Value),
            StringUnion su => su.Members.Count <= 3
                ? string.Concat(su.Members.Select(s => char.ToUpperInvariant(s[0]) + s[1..]))
                : "Enum",
            IntUnion => "Enum",
            Literal literal => LiteralNameSuffix(literal.Value),
            // Field TYPES are part of the suffix: naming by field names alone made
            // Wrapper<{value:string}> and Wrapper<{value:number}> collide on "Wrapper_Value"
            // and silently overwrite each other's component schema (E2).
            InlineObject obj => obj.Fields.Count <= 3
                ? string.Join(
                    "_",
                    obj.Fields.Select(f =>
                        char.ToUpperInvariant(f.Name[0]) + f.Name[1..] + "_" + GetNameSuffix(f.Type)
                    )
                )
                : "Object",
            TaggedUnion tu => char.ToUpperInvariant(tu.Discriminator[0])
                + tu.Discriminator[1..]
                + "Union",
            Union u => string.Concat(u.Variants.Select(GetNameSuffix)) + "Union",
            _ => "Unknown",
        };
    }

    /// <summary>
    /// Produces a monomorphised name for a generic type: "PagedResult_TaskDto".
    /// </summary>
    public static string MonomorphisedName(Generic g)
    {
        return g.Name + "_" + string.Join("_", g.TypeArguments.Select(GetNameSuffix));
    }

    /// <summary>
    /// Recursively resolves type parameters in a TsType tree using the given map.
    /// </summary>
    public static TsType ResolveTypeParams(TsType type, Dictionary<string, TsType> map)
    {
        return type switch
        {
            TypeParam tp when map.TryGetValue(tp.Name, out var resolved) => resolved,
            Array a => new Array(ResolveTypeParams(a.Element, map), a.ElementMetadata),
            Nullable n => new Nullable(ResolveTypeParams(n.Inner, map)),
            Dictionary d => new Dictionary(
                ResolveTypeParams(d.Value, map),
                d.Key is null ? null : ResolveTypeParams(d.Key, map),
                d.ValueMetadata
            ),
            Generic g => new Generic(
                g.Name,
                g.TypeArguments.Select(a => ResolveTypeParams(a, map)).ToList()
            ),
            InlineObject obj => new InlineObject(
                obj.Fields.Select(f => new InlineObjectField(
                        f.Name,
                        ResolveTypeParams(f.Type, map),
                        f.Optional
                    ))
                    .ToList()
            ),
            TaggedUnion tu => new TaggedUnion(
                tu.Discriminator,
                tu.Variants.Select(v => new TaggedUnionVariant(
                        v.Tag,
                        ResolveTypeParams(v.Type, map),
                        v.Metadata
                    ))
                    .ToList()
            ),
            Union u => new Union(u.Variants.Select(v => ResolveTypeParams(v, map)).ToList()),
            _ => type,
        };
    }

    /// <summary>
    /// Recursively collects all named type references from a TsType tree.
    /// </summary>
    public static void CollectTypeRefs(TsType type, HashSet<string> names)
    {
        switch (type)
        {
            case TypeRef r:
                names.Add(r.Name);
                break;
            case Nullable n:
                CollectTypeRefs(n.Inner, names);
                break;
            case Array a:
                CollectTypeRefs(a.Element, names);
                break;
            case Dictionary d:
                CollectTypeRefs(d.Value, names);
                if (d.Key is not null)
                {
                    CollectTypeRefs(d.Key, names);
                }
                break;
            case Generic g:
                names.Add(g.Name);
                foreach (var arg in g.TypeArguments)
                {
                    CollectTypeRefs(arg, names);
                }
                break;
            case Brand b:
                names.Add(b.Name);
                CollectTypeRefs(b.Inner, names);
                break;
            case StringUnion:
            case IntUnion:
            case Literal:
            case Primitive:
            case TypeParam:
                // No type refs to collect
                break;
            case InlineObject obj:
                foreach (var field in obj.Fields)
                {
                    CollectTypeRefs(field.Type, names);
                }
                break;
            case TaggedUnion tu:
                foreach (var variant in tu.Variants)
                {
                    CollectTypeRefs(variant.Type, names);
                }
                break;
            case Union u:
                foreach (var variant in u.Variants)
                {
                    CollectTypeRefs(variant, names);
                }
                break;
        }
    }

    private static string LiteralNameSuffix(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => "Literal" + value.GetString(),
            JsonValueKind.Number => "Literal"
                + value.GetRawText().Replace("-", "Negative", StringComparison.Ordinal),
            JsonValueKind.True => "LiteralTrue",
            JsonValueKind.False => "LiteralFalse",
            _ => "Literal",
        };
}
