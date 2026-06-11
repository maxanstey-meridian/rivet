# Type Mapping

How C# types lower into OpenAPI 3.1 schemas. Property names camelCase by default
(`[JsonPropertyName]` overrides); non-nullable, non-`[RivetOptional]` members are
`required`.

## Primitives

| C# | JSON Schema |
|---|---|
| `string` | `string` |
| `bool` | `boolean` |
| `int` / `uint` / `long` / `ulong` / `short` / `ushort` / `byte` / `sbyte` | `integer` with `format: int32/uint32/int64/uint64/int16/uint16/uint8/int8` (plus min/max bounds for the common widths) |
| `float` / `double` / `decimal` | `number` with `format: float/double/decimal` |
| `Guid` | `string`, `format: uuid` |
| `DateTime` / `DateTimeOffset` | `string`, `format: date-time` (`DateTimeOffset` additionally carries `x-rivet-csharp-type` so the import round-trip recovers the exact type) |
| `DateOnly` | `string`, `format: date` |
| `TimeOnly` | `string`, `format: time` |
| `Uri` | `string`, `format: uri` |
| `byte[]` | `string`, `contentEncoding: base64` (the OpenAPI 3.1 idiom — matches the System.Text.Json wire format), plus `x-rivet-csharp-type` |

## Composites

- **Records / classes** → `object` schemas in `components/schemas` with
  `properties` + `required`.
- **Enums** → `string` schemas with camelCased `enum` values
  (`{ Draft, Open }` → `["draft", "open"]` — pair with a camelCase
  `JsonStringEnumConverter` at runtime).
- **Nullable members** (`string?`, `int?`) → 3.1 type arrays
  (`"type": ["string", "null"]`); nullable `$ref`s use a null branch.
- **Collections** (`List<T>`, `IReadOnlyList<T>`, arrays) → `array` with `items`.
- **Dictionaries** → `object` with `additionalProperties`. Non-string keys add a
  `propertyNames` schema: enum keys `$ref` the enum schema (which is emitted —
  `Dictionary<Color, int>` registers `Color`), string-backed brand keys `$ref` the
  brand schema, and string-serializable primitive keys (`Guid`, `DateTime`/`DateOnly`/
  `TimeOnly`, `Uri`, numerics) emit `type: string` with the original `format` plus
  `x-rivet-csharp-type` where the format alone is ambiguous. Unsupported key types
  degrade to unconstrained string keys with diagnostic `RIV1013`.
- **Generics** are monomorphised: `PagedResult<MemberDto>` becomes a
  `PagedResult_MemberDto` component carrying `x-rivet-generic`.
- **Value-object brands**: a record with exactly one property named `Value`
  (e.g. `record Email(string Value)`) lowers to its inner primitive with
  `x-rivet-brand` — `{ "type": "string", "x-rivet-brand": "Email" }`. On the wire
  it is just the primitive.
- **Polymorphic hierarchies** (`[JsonPolymorphic]`/`[JsonDerivedType]` on a base
  type) → `oneOf` + `discriminator` with a complete tag → `$ref` `mapping`, named
  after the base. Each registration becomes a `{Base}_{Tag}` variant component:
  the discriminator property first (default `$type`, single-value `enum`,
  required), then the derived type's full flattened property surface — matching
  System.Text.Json's wire output when serializing *as the base type*. A derived
  type referenced directly keeps its own untagged schema (STJ writes no
  discriminator for it). Non-string tags (`RIV1014`) and registration-less
  `[JsonPolymorphic]` (`RIV1015`) fall back to plain flattening, loudly;
  `UnknownDerivedTypeHandling` has no spec representation (`RIV1016`).

## Constraint and metadata flow

DataAnnotations and Rivet attributes enrich property schemas: `minLength`,
`maxLength`, `pattern`, `minimum`/`maximum`, `exclusiveMinimum`/`exclusiveMaximum`,
`multipleOf`, `minItems`/`maxItems`/`uniqueItems`, `description`, `default`,
`examples`, `deprecated`, `readOnly`/`writeOnly`, `format`. See
[Attributes](/reference/attributes) for which attribute produces which keyword —
and note these are spec-only; Rivet does not enforce them at runtime
([Runtime Validation](/guides/runtime-validation)).
