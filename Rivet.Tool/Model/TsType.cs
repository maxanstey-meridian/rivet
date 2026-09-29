using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rivet.Tool.Model;

/// <summary>
/// Intermediate representation of a schema type.
/// Produced by the type walker, consumed by the emitter. The JSON shape (discriminated by
/// <c>kind</c>) is the contract-JSON wire format in <c>rivet-contract-schema.json</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Primitive), "primitive")]
[JsonDerivedType(typeof(Nullable), "nullable")]
[JsonDerivedType(typeof(Array), "array")]
[JsonDerivedType(typeof(Dictionary), "dictionary")]
[JsonDerivedType(typeof(StringUnion), "stringUnion")]
[JsonDerivedType(typeof(IntUnion), "intUnion")]
[JsonDerivedType(typeof(Literal), "literal")]
[JsonDerivedType(typeof(TypeRef), "ref")]
[JsonDerivedType(typeof(Generic), "generic")]
[JsonDerivedType(typeof(TypeParam), "typeParam")]
[JsonDerivedType(typeof(Brand), "brand")]
[JsonDerivedType(typeof(InlineObject), "inlineObject")]
[JsonDerivedType(typeof(TaggedUnion), "taggedUnion")]
[JsonDerivedType(typeof(Union), "union")]
public abstract record TsType
{
    private TsType() { }

    /// <summary>Leaf type: "string", "number", "boolean", "unknown". Optional Format for OpenAPI/JSON Schema.
    /// CSharpType is set when the C# type can't be recovered from Name+Format alone (e.g. DateTimeOffset, uint).</summary>
    public sealed record Primitive(
        [property: JsonRequired, JsonPropertyName("type")] string Name,
        string? Format = null,
        [property: JsonPropertyName("csharpType")] string? CSharpType = null
    ) : TsType;

    /// <summary>T | null.</summary>
    public sealed record Nullable([property: JsonRequired] TsType Inner) : TsType;

    /// <summary>T[].</summary>
    public sealed record Array(
        [property: JsonRequired] TsType Element,
        TsScalarMetadata? ElementMetadata = null
    ) : TsType;

    /// <summary>Record&lt;string, T&gt;. Key is null for plain string keys; otherwise the
    /// key's contract representation (enum ref, string-backed brand, or a string-typed
    /// primitive carrying the original format/CSharpType) — emitted as propertyNames.</summary>
    public sealed record Dictionary(
        [property: JsonRequired] TsType Value,
        TsType? Key = null,
        TsScalarMetadata? ValueMetadata = null
    ) : TsType;

    /// <summary>"A" | "B" | "C" — string enum rendered as union. NamingPolicy names
    /// the casing convention the source enum's Rivet*EnumConverter family converter
    /// declares ("camelCase" etc.); present so the import round-trip re-derives the
    /// wire values from the policy instead of blanket member pins.</summary>
    public sealed record StringUnion(
        [property: JsonRequired, JsonPropertyName("values")] IReadOnlyList<string> Members,
        TsTypeMetadata? Metadata = null,
        string? Format = null,
        string? Description = null,
        TsScalarMetadata? ScalarMetadata = null,
        string? NamingPolicy = null
    ) : TsType;

    /// <summary>1 | 2 | 3 — int enum rendered as numeric literal union. Members are
    /// decimal string literals so every legal C# enum constant survives without an
    /// Int32 assumption (long/ulong enums, negative values) — emitters parse them to
    /// wide integers for exact digit output.</summary>
    public sealed record IntUnion(
        [property:
            JsonRequired,
            JsonPropertyName("values"),
            JsonConverter(typeof(IntEnumValuesJsonConverter))
        ]
            IReadOnlyList<string> Members,
        string? Format = null,
        TsTypeMetadata? Metadata = null,
        string? Description = null,
        TsScalarMetadata? ScalarMetadata = null
    ) : TsType;

    /// <summary>A JSON scalar literal type represented with OpenAPI 3.1 const.</summary>
    public sealed record Literal([property: JsonRequired] JsonElement Value) : TsType;

    /// <summary>Reference to another emitted type by name.</summary>
    public sealed record TypeRef([property: JsonRequired] string Name) : TsType;

    /// <summary>Generic type application: Foo&lt;T, U&gt;.</summary>
    public sealed record Generic(
        [property: JsonRequired] string Name,
        [property: JsonRequired, JsonPropertyName("typeArgs")] IReadOnlyList<TsType> TypeArguments
    ) : TsType;

    /// <summary>Unresolved generic type parameter: T, TKey, etc.</summary>
    public sealed record TypeParam([property: JsonRequired] string Name) : TsType;

    /// <summary>Branded primitive: string &amp; { readonly __brand: "Email" }.</summary>
    public sealed record Brand(
        [property: JsonRequired] string Name,
        [property: JsonRequired, JsonPropertyName("underlying")] TsType Inner,
        TsTypeMetadata? Metadata = null,
        string? Description = null
    ) : TsType;

    /// <summary>
    /// The wire surface an inline-object field participates in. Polymorphic variants are
    /// the shared wire shape for both directions; a derived property System.Text.Json only
    /// serializes (or only deserializes) keeps its variant presence with the asymmetry
    /// expressed as readOnly/writeOnly on emission.
    /// </summary>
    public enum InlineObjectFieldSurface
    {
        Both,
        RequestOnly,
        ResponseOnly,
    }

    /// <summary>Inline object: { key: string; value?: number }. Used for tuples and union variants.</summary>
    public sealed record InlineObject(
        [property: JsonRequired, JsonPropertyName("properties")]
            IReadOnlyList<InlineObjectField> Fields
    ) : TsType;

    [JsonConverter(typeof(InlineObjectFieldJsonConverter))]
    public sealed record InlineObjectField(
        string Name,
        TsType Type,
        bool Optional = false,
        InlineObjectFieldSurface Surface = InlineObjectFieldSurface.Both
    )
    {
        // Keep concise tuple syntax without conflating a nullable value with an absent field.
        public static implicit operator InlineObjectField((string Name, TsType Type) field) =>
            new(field.Name, field.Type, Optional: false);
    }

    /// <summary>Discriminated union of object-like variants keyed by a shared string-literal field.</summary>
    public sealed record TaggedUnion(
        [property: JsonRequired] string Discriminator,
        [property: JsonRequired] IReadOnlyList<TaggedUnionVariant> Variants
    ) : TsType;

    /// <summary>An undiscriminated union (oneOf without discriminator) — [RivetUnion] wrappers.</summary>
    public sealed record Union([property: JsonRequired] IReadOnlyList<TsType> Variants) : TsType;

    public sealed record TaggedUnionVariant(
        [property: JsonRequired] string Tag,
        [property: JsonRequired] TsType Type,
        TsTypeMetadata? Metadata = null
    );

    /// <summary>
    /// An absent <c>optional</c> means the field is optional exactly when its type is nullable.
    /// </summary>
    private sealed class InlineObjectFieldJsonConverter : JsonConverter<InlineObjectField>
    {
        private sealed record Wire(
            [property: JsonRequired] string Name,
            [property: JsonRequired] TsType Type,
            bool? Optional,
            [property:
                JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault),
                JsonConverter(typeof(JsonStringEnumConverter<InlineObjectFieldSurface>))
            ]
                InlineObjectFieldSurface Surface = InlineObjectFieldSurface.Both
        );

        public override InlineObjectField Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            var wire =
                JsonSerializer.Deserialize<Wire>(ref reader, options)
                ?? throw new JsonException("An inline object property must be a JSON object.");
            return new InlineObjectField(
                wire.Name,
                wire.Type,
                wire.Optional ?? wire.Type is Nullable,
                wire.Surface
            );
        }

        public override void Write(
            Utf8JsonWriter writer,
            InlineObjectField value,
            JsonSerializerOptions options
        ) =>
            JsonSerializer.Serialize(
                writer,
                new Wire(value.Name, value.Type, value.Optional, value.Surface),
                options
            );
    }

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
            // and silently overwrite each other's component schema.
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
    /// Replaces every unresolved type parameter found in <paramref name="map"/>.
    /// </summary>
    public static TsType ResolveTypeParams(TsType type, Dictionary<string, TsType> map) =>
        type.Rewrite(node =>
            node is TypeParam tp && map.TryGetValue(tp.Name, out var resolved) ? resolved : null
        );

    /// <summary>The directly nested types, in declaration order (dictionary value before key).</summary>
    public IEnumerable<TsType> Children() =>
        this switch
        {
            Nullable n => [n.Inner],
            Array a => [a.Element],
            Dictionary d => d.Key is null ? [d.Value] : [d.Value, d.Key],
            Generic g => g.TypeArguments,
            Brand b => [b.Inner],
            InlineObject obj => obj.Fields.Select(field => field.Type),
            TaggedUnion tu => tu.Variants.Select(variant => variant.Type),
            Union u => u.Variants,
            _ => [],
        };

    /// <summary>This type followed by every nested type, pre-order.</summary>
    public IEnumerable<TsType> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children())
        {
            foreach (var descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Rebuilds the tree top-down. Where <paramref name="replace"/> returns a type, that type
    /// is used as-is; otherwise the node is copied with its children rewritten, so every
    /// other record member (metadata, descriptions) is preserved.
    /// </summary>
    public TsType Rewrite(Func<TsType, TsType?> replace) =>
        replace(this)
        ?? this switch
        {
            Nullable n => n with { Inner = n.Inner.Rewrite(replace) },
            Array a => a with { Element = a.Element.Rewrite(replace) },
            Dictionary d => d with
            {
                Value = d.Value.Rewrite(replace),
                Key = d.Key?.Rewrite(replace),
            },
            Generic g => g with
            {
                TypeArguments = g.TypeArguments.Select(arg => arg.Rewrite(replace)).ToList(),
            },
            Brand b => b with { Inner = b.Inner.Rewrite(replace) },
            InlineObject obj => obj with
            {
                Fields = obj
                    .Fields.Select(field => field with { Type = field.Type.Rewrite(replace) })
                    .ToList(),
            },
            TaggedUnion tu => tu with
            {
                Variants = tu
                    .Variants.Select(variant =>
                        variant with
                        {
                            Type = variant.Type.Rewrite(replace),
                        }
                    )
                    .ToList(),
            },
            Union u => u with
            {
                Variants = u.Variants.Select(variant => variant.Rewrite(replace)).ToList(),
            },
            _ => this,
        };

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
