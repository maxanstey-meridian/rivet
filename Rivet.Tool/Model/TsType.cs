using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Inline object: { key: string; value?: number }. Used for tuples and union variants.</summary>
    public sealed record InlineObject(
        [property: JsonRequired, JsonPropertyName("properties")]
            IReadOnlyList<InlineObjectField> Fields
    ) : TsType;

    /// <summary>
    /// One inline-object property. An absent <c>optional</c> reads as required: a nullable
    /// type does not make the property optional. The annotations after <c>optional</c> are a
    /// record property's (<see cref="TsPropertyDefinition"/>, same JSON names), so a
    /// polymorphic variant describes its members exactly as the derived record would; a member
    /// System.Text.Json only reads (or only writes) is writeOnly (readOnly) here too.
    /// </summary>
    public sealed record InlineObjectField(
        [property: JsonRequired] string Name,
        [property: JsonRequired] TsType Type,
        bool Optional = false,
        [property:
            JsonPropertyName("deprecated"),
            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
        ]
            bool IsDeprecated = false,
        string? Format = null,
        string? DefaultValue = null,
        TsPropertyConstraints? Constraints = null,
        string? Description = null,
        string? Example = null,
        [property:
            JsonPropertyName("readOnly"),
            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
        ]
            bool IsReadOnly = false,
        [property:
            JsonPropertyName("writeOnly"),
            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)
        ]
            bool IsWriteOnly = false,
        TsScalarMetadata? ScalarMetadata = null
    )
    {
        // Keep concise tuple syntax without conflating a nullable value with an absent field.
        public static implicit operator InlineObjectField((string Name, TsType Type) field) =>
            new(field.Name, field.Type, Optional: false);

        public static InlineObjectField FromProperty(TsPropertyDefinition property) =>
            new(
                property.Name,
                property.Type,
                property.IsOptional,
                property.IsDeprecated,
                property.Format,
                property.DefaultValue,
                property.Constraints,
                property.Description,
                property.Example,
                property.IsReadOnly,
                property.IsWriteOnly,
                property.ScalarMetadata
            );

        public TsPropertyDefinition ToProperty() =>
            new(
                Name,
                Type,
                Optional,
                IsDeprecated,
                Format,
                DefaultValue,
                Constraints,
                Description,
                Example,
                IsReadOnly,
                IsWriteOnly,
                ScalarMetadata
            );
    }

    /// <summary>Discriminated union of object-like variants keyed by a shared string-literal field.</summary>
    public sealed record TaggedUnion(
        [property: JsonRequired] string Discriminator,
        [property: JsonRequired] IReadOnlyList<TaggedUnionVariant> Variants
    ) : TsType;

    /// <summary>An undiscriminated union (oneOf without discriminator) — [RivetUnion] wrappers.</summary>
    public sealed record Union([property: JsonRequired] IReadOnlyList<TsType> Variants) : TsType;

    /// <summary>
    /// One registration of a tagged union. <c>Description</c> and <c>ScalarMetadata</c> are the
    /// derived type's own type-level annotations, as a <see cref="TsTypeDefinition"/> carries
    /// them, so an inline variant's component says what the derived type's would.
    /// <c>DiscriminatorSchema</c> is the JSON of its tag property's schema as the source
    /// authored it (a plain <c>{ "type": "string" }</c> with a description, say), emitted in
    /// place of <c>enum: [tag]</c>. All three need an inline object carrying the tag property
    /// to sit on.
    /// </summary>
    public sealed record TaggedUnionVariant(
        [property: JsonRequired] string Tag,
        [property: JsonRequired] TsType Type,
        TsTypeMetadata? Metadata = null,
        string? Description = null,
        TsScalarMetadata? ScalarMetadata = null,
        string? DiscriminatorSchema = null
    )
    {
        /// <summary>
        /// Why <paramref name="json"/> can't be the schema of the tag property that carries
        /// <paramref name="tag"/>, or null when it can: it must be a JSON object, an
        /// <c>enum</c> or <c>const</c> it has must admit the tag, and a <c>type</c> it has
        /// must include <c>string</c>. A <c>$ref</c> is taken on trust.
        /// </summary>
        public static string? TagSchemaProblem(string json, string tag)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return "isn't valid JSON";
            }

            if (node is not JsonObject schema)
            {
                return "isn't a JSON object";
            }

            bool IsTag(JsonNode? value) =>
                value is JsonValue scalar
                && scalar.TryGetValue<string>(out var text)
                && text == tag;
            bool IsString(JsonNode? value) =>
                value is JsonValue scalar
                && scalar.TryGetValue<string>(out var text)
                && text == "string";

            if (
                schema.TryGetPropertyValue("enum", out var allowed)
                && !(allowed is JsonArray values && values.Any(IsTag))
            )
            {
                return $"has an enum that doesn't list the tag '{tag}'";
            }

            if (schema.TryGetPropertyValue("const", out var constant) && !IsTag(constant))
            {
                return $"has a const other than the tag '{tag}'";
            }

            if (
                schema.TryGetPropertyValue("type", out var type)
                && !(IsString(type) || type is JsonArray types && types.Any(IsString))
            )
            {
                return "has a type that doesn't include string, though the tag is one";
            }

            return null;
        }
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
