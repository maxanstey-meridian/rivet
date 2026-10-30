# Type Mapping

How C# types lower into OpenAPI 3.1 schemas. Property names camelCase by default
(`[JsonPropertyName]` overrides). Every object property is `required` unless it is
marked `[RivetOptional]` or `[JsonIgnore(Condition = WhenWritingNull | WhenWritingDefault)]`.
Nullability does not make a property optional: System.Text.Json writes `null`, so a
`string?` property is required with type `["string", "null"]`. Query, header and form
parameters follow the model binder instead: a nullable parameter is optional.

`[JsonRequired]` and C# `required` keep a property required, which it already was. What
they can't do is sit on a property that's also allowed to go missing: `[RivetOptional]`, or
a `[JsonIgnore]` condition that leaves it out when it's null or default. The host would then
refuse a request without the member while the spec says it's optional, and one schema can't
say both, so generation stops (`RIV1119`) and the message tells you which of the two to drop.

## Primitives

| C# | JSON Schema |
|---|---|
| `string` | `string` |
| `bool` | `boolean` |
| `int` / `uint` / `long` / `ulong` / `short` / `ushort` / `byte` / `sbyte` | `integer` with `format: int32/uint32/int64/uint64/int16/uint16/uint8/int8` (plus min/max bounds for the common widths) |
| `float` / `double` / `decimal` | `number` with `format: float/double/decimal` |
| `Int128` / `UInt128` | `integer` with `x-rivet-csharp-type: Int128/UInt128` (System.Text.Json writes them as JSON numbers; no OpenAPI format names them) |
| `Half` | `number` with `x-rivet-csharp-type: Half` |
| `Guid` | `string`, `format: uuid` |
| `DateTime` / `DateTimeOffset` | `string`, `format: date-time` (`DateTimeOffset` additionally carries `x-rivet-csharp-type` so the import round-trip recovers the exact type) |
| `DateOnly` | `string`, `format: date` |
| `TimeOnly` | `string`, `format: time` |
| `Uri` | `string`, `format: uri` |
| `byte[]` | `string`, `contentEncoding: base64` (the OpenAPI 3.1 idiom — matches the System.Text.Json wire format), plus `x-rivet-csharp-type` |
| `char` | `string` with `minLength: 1` and `maxLength: 1` (System.Text.Json writes a single-character JSON string), plus `x-rivet-csharp-type` so the import round-trip recovers `char` |
| `TimeSpan` | `string` matching System.Text.Json's invariant `"c"` format (`[-][d.]hh:mm:ss[.fffffff]`), plus `x-rivet-csharp-type: TimeSpan` |
| `BigInteger` | refused (`RIV1010`): System.Text.Json has no converter for it, so expose a string of digits or a bounded number |
| `object` | untyped (empty) schema — "any JSON value", emitted deliberately and with no diagnostic. openapi-typescript consumers see `unknown`; cast at the boundary. |
| `JsonElement` / `JsonDocument` / `JsonNode` / `JsonValue` | the same untyped schema, with no diagnostic: they hold any JSON value too (`JsonNode` adds `x-rivet-csharp-type` so an import gets it back) |
| `JsonObject` / `JsonArray` | `object` with any-valued `additionalProperties` / `array` with any-valued `items`, plus `x-rivet-csharp-type` |

## Composites

- **Records / classes** → `object` schemas in `components/schemas` with
  `properties` + `required`.
- **Enums** → `integer` schemas by default, matching ordinary System.Text.Json
  serialization of unannotated enums (`{ Draft, Open }` → values 0 and 1). A
  type-level `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` opts the enum
  into `string` schemas with the exact C# member names as values (no naming
  policy writes CLR names verbatim). One of the `Rivet*EnumConverter` family
  members (`RivetLowerCaseEnumConverter<T>`, `RivetCamelCaseEnumConverter<T>`,
  `RivetSnakeCaseEnumConverter<T>`, `RivetKebabCaseEnumConverter<T>`) opts into a
  cased string union — the casing lives in the converter class name, and the
  emitted contract values equal the runtime serializer's wire values by
  construction (the same `JsonNamingPolicy` instance drives both). A per-member
  `[JsonStringEnumMemberName]` overrides the converter's casing. When the casing
  produces the same wire value for two members (`RIV1106`), generation fails — loudly, never with a wrong string union.
  On the way in, the family reads exactly the names it writes, case-sensitively, on
  every runtime. Integers, numeric strings and the C# member name in any other
  casing are a deserialization error (see
  [Limitations](/misc/limitations#json-bodies-take-exactly-the-spec-s-names)). The
  bare built-in `JsonStringEnumConverter<T>` keeps STJ's default and quietly
  accepts integers its string spec never mentions.
  All of this is the JSON body only: route, query, header and form values are bound
  by ASP.NET, which never asks the converter. An enum parameter still gets the
  converter's names in the spec, so Rivet checks the host can bind them (`RIV4005`,
  `RIV1110`).
- **Nullable members** (`string?`, `int?`) → 3.1 type arrays
  (`"type": ["string", "null"]`); nullable `$ref`s use a null branch. They stay in
  `required`; add `[RivetOptional]` for a property clients may omit.
- **Collections** (`List<T>`, `IReadOnlyList<T>`, arrays) → `array` with `items`.
- **Dictionaries** → `object` with `additionalProperties`. Non-string keys add a
  `propertyNames` schema: enum keys `$ref` the enum schema (which is emitted —
  `Dictionary<Color, int>` registers `Color`), string-backed brand keys `$ref` the
  brand schema, and string-serializable primitive keys (`Guid`, `DateTime`/`DateOnly`/
  `TimeOnly`, `TimeSpan`, `Uri`, `char`, numerics) emit `type: string` with the original `format`
  (`char` keys carry the length-1 bounds instead) plus `x-rivet-csharp-type` where the
  format alone is ambiguous. Unsupported key types
  degrade to unconstrained string keys with diagnostic `RIV1013`.
- **Generics** are monomorphised: `PagedResult<MemberDto>` becomes a
  `PagedResult_MemberDto` component carrying `x-rivet-generic`.
- **Value-object brands**: an explicit `[RivetScalar]` on a record/class/struct
  with exactly one property named `Value` (e.g. `[RivetScalar] record Email(string
  Value)`) lowers to its inner primitive with `x-rivet-brand` —
  `{ "type": "string", "x-rivet-brand": "Email" }`. On the wire it is just the
  primitive. Without `[RivetScalar]` the same record is an ordinary object schema
  (the shape alone no longer decides).
- **Polymorphic hierarchies** (`[JsonPolymorphic]`/`[JsonDerivedType]` on a base
  type) → `oneOf` + `discriminator` with a complete tag → `$ref` `mapping`, named
  after the base. Each registration becomes a `{Base}_{Tag}` variant component (with a numeric suffix if that name is already taken):
  the discriminator property first (default `$type`, single-value `enum`,
  required), then the derived type's full flattened property surface — matching
  System.Text.Json's wire output when serializing *as the base type*. A variant
  is the derived record's own schema with the tag in front: the record's
  description, and on every field what its property would say (description,
  constraints, default, example, format, `deprecated`, and `readOnly`/`writeOnly`
  for a member System.Text.Json only writes or only reads). A derived
  type referenced directly keeps its own untagged schema (STJ writes no
  discriminator for it). Int or missing tags stop generation (`RIV1014`), as does
  registering something that isn't a named type at all; so does a `[JsonPolymorphic]`
  with no registrations (`RIV1015`), which System.Text.Json refuses to serialize at all,
  and an `UnknownDerivedTypeHandling` fallback or `IgnoreUnrecognizedTypeDiscriminators`
  (`RIV1016`), because the host would then write or read bodies the `oneOf` doesn't
  admit. Those last two hold for a generic base too, even though a generic base still
  flattens to its own members rather than becoming a `oneOf`.

  Imported hierarchies keep what their source said. A variant generated with a
  `[RivetGeneratedType]` component id is emitted under that id instead of
  `{Base}_{Tag}`, and one whose `DiscriminatorSchemaJson` records the source's tag
  schema gets that schema back instead of the synthesized `enum`. That JSON has to be a
  schema object the tag satisfies: invalid JSON, an `enum` or `const` that leaves the tag
  out, or a `type` without `string`, stops generation (`RIV1122`), since the spec would
  then refuse the very tag the host writes. So does a `DiscriminatorSchemaJson` on a type
  no base registers with `[JsonDerivedType]`: there's no tag property for it to describe,
  so re-import or delete it. A base whose source schema was preserved verbatim
  (Bitbucket's `allOf` hierarchy hanging off one `object` with a bare `discriminator`, or
  Jira's `oneOf`, say) is emitted as exactly that schema, with nothing synthesized beside
  it: no `oneOf` of Rivet's own and no `{Base}_{Tag}` inventions. Variants it names that
  weren't preserved are still built from their records, under the source's ids.

## Open and closed objects

An object schema says nothing about members beyond its properties unless the type does:

- **`[JsonExtensionData]`** opens it. System.Text.Json writes every entry of the bag as a
  member of the object and reads every member it doesn't recognise into it, so the schema
  says `additionalProperties: {}`: "anything else goes", which generators turn into an
  index signature rather than a closed type. The bag itself never appears as a property.
  A base type's bag opens every type that inherits it, polymorphic variants included. The
  bag has to be something System.Text.Json takes: a `Dictionary<string, JsonElement>`, a
  `Dictionary<string, object>` (or anything else implementing `IDictionary<string,
  JsonElement>` or `IDictionary<string, object>`), or a `JsonObject`. Anything else (an
  `IReadOnlyDictionary`, a `Dictionary<string, string>`), or a second bag anywhere down
  the inheritance chain, would blow up at runtime, so it stops generation instead
  (`RIV1118`). A private bag with no `[JsonInclude]` is invisible to the serializer and
  opens nothing.
- **`[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]`** closes it: the host
  refuses a request carrying a member the type doesn't declare, so the schema says
  `additionalProperties: false`. The attribute isn't inherited (System.Text.Json reads it off
  the type itself), so neither is the `false`. On a polymorphic variant it's fine: the
  discriminator is one of the variant's own properties. Disallow beside a bag is a pair
  System.Text.Json refuses outright, and so does Rivet (`RIV1118`): the bag exists to take
  exactly the members Disallow turns away. The host-wide `UnmappedMemberHandling` option is
  a different story: generation can't see it, so the runtime adapters refuse it (see
  [Runtime Validation](/guides/runtime-validation)).

Contract JSON (`--from`) says the same with `"additionalProperties": true | false` on an
object type or an inline object.

An imported record gets a bag whenever its source admits extra members, so the server keeps
whatever a client sends. Whether the spec says so is the source's call, as with every other
keyword an import preserves: a schema that leaves `additionalProperties` out (which JSON Schema
reads as "anything else goes") comes back without it, and one that writes
`additionalProperties: {}` comes back with it. The importer records which on the type
(`[RivetGeneratedType(..., DeclaresAdditionalProperties = true)]`).

## Constraint and metadata flow

DataAnnotations and Rivet attributes enrich property schemas: `minLength`,
`maxLength`, `pattern`, `minimum`/`maximum`, `exclusiveMinimum`/`exclusiveMaximum`,
`multipleOf`, `minItems`/`maxItems`/`uniqueItems`, `description`, `default`,
`examples`, `deprecated`, `readOnly`/`writeOnly`, `format`. See
[Attributes](/reference/attributes) for which attribute produces which keyword —
and note these are spec-only; Rivet does not enforce them at runtime
([Runtime Validation](/guides/runtime-validation)).

`[RivetSchemaType]` re-types a primitive: an unformatted `long` that should say `integer`,
a `JsonElement` that's always an `object`. It needs a primitive (or nullable primitive) to
work on, so on a member whose type is a record, a collection, an enum or a `[RivetScalar]`
brand it would have nothing to change and used to vanish without a word. Now it stops
generation (`RIV1117`). If the type really is a string (or another scalar) on the wire,
say so on the type: make it a `[RivetScalar]` record with a single `Value` property, and
every member that uses it gets the scalar's schema.
