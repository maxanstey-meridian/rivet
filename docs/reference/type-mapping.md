# Type Mapping

## C# → TypeScript → OpenAPI

| C# | TypeScript | OpenAPI JSON Schema |
|---|---|---|
| `string` | `string` | `type: string` |
| `Guid` | `string` | `type: string, format: uuid` |
| `int` | `number` | `type: integer, format: int32` |
| `long` | `number` | `type: integer, format: int64` |
| `double` | `number` | `type: number, format: double` |
| `float` | `number` | `type: number, format: float` |
| `decimal` | `number` | `type: number, format: decimal` |
| `uint`, `ulong` | `number` | `type: integer` |
| `bool` | `boolean` | `type: boolean` |
| `DateTime` | `string` | `type: string, format: date-time` |
| `DateTimeOffset` | `string` | `type: string, format: date-time` |
| `DateOnly` | `string` | `type: string, format: date` |
| `T?` (nullable value/ref) | `T \| null` | `nullable: true` |
| `List<T>`, `T[]`, `IEnumerable<T>`, `IReadOnlyList<T>` | `T[]` | `type: array, items: {...}` |
| `Dictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `Record<string, T>` | `type: object, additionalProperties: {...}` |
| `sealed record` | `type { ... }` (transitive discovery, including project references) | `type: object, properties: {...}` |
| `enum` (with `JsonStringEnumConverter`) | `type Status = "A" \| "B"` | `type: string, enum: [...]` |
| `PagedResult<T>` (generic record) | `PagedResult<T>` | Monomorphised: `PagedResult_TaskDto` + `x-rivet-generic` |
| `JsonElement`, `JsonNode` | `unknown` | `{}` |
| `JsonObject` | `Record<string, unknown>` | `type: object` |
| `JsonArray` | `unknown[]` | `type: array` |
| `Email(string Value)` (single-property VO) | `string & { readonly __brand: "Email" }` | `$ref` to component schema with `x-rivet-brand` |

::: info Note
The OpenAPI emitter preserves `format` metadata from the intermediate type model. `int` emits as `type: integer, format: int32`, `DateTime` as `type: string, format: date-time`, etc. This enables lossless round-trips for most primitive types. Branded value objects are emitted as component schemas with `x-rivet-brand` so they survive [round-trips](/guides/openapi-round-trips).
:::

## Cross-project type discovery

Types referenced by contracts or endpoints are discovered transitively — even from project references. If your domain types live in a separate `.csproj` that your API project references, Rivet walks them automatically. No `[RivetType]` attribute needed on transitively-discovered types. NuGet and framework types are not walked.

## Value objects

Single-property records with a property named `Value` are emitted as branded types:

```csharp
// C#
public sealed record Email(string Value);
public sealed record Uprn(string Value);
public sealed record Quantity(int Value);
```

```typescript
// TypeScript — branded primitives, nominal type safety
export type Email = string & { readonly __brand: "Email" };
export type Uprn = string & { readonly __brand: "Uprn" };
export type Quantity = number & { readonly __brand: "Quantity" };
```

Multi-property records are emitted as regular object types: `Money(decimal Amount, string Currency)` becomes `{ amount: number; currency: string }`.

## OpenAPI → C# (import direction)

| JSON Schema | C# |
|---|---|
| `string` | `string` |
| `string` + `format: date-time` | `DateTime` |
| `string` + `format: date` | `DateOnly` |
| `string` + `format: guid` / `uuid` | `Guid` |
| `string` + `format: email`, `uri`, etc. | Branded value object |
| `string` + `enum: [...]` | `enum` |
| `integer` / `integer` + `format: int32` | `int` |
| `integer` + `format: int64` | `long` |
| `number` / `number` + `format: double` | `double` |
| `number` + `format: float` | `float` |
| `number` + `format: decimal` | `decimal` |
| `boolean` | `bool` |
| Any type + `x-rivet-csharp-type` | Exact C# type (`uint`, `DateTimeOffset`, `short`, etc.) |
| Property with `deprecated: true` | `[Obsolete]` attribute |
| `array` + `items` | `List<T>` |
| `object` + non-empty `properties` | `sealed record` |
| `object` + `additionalProperties` | `Dictionary<string, T>` |
| `object` with no `properties` | `Dictionary<string, JsonElement>` (no record generated) |

> **Note:** Empty `properties: {}` is treated the same as absent `properties` — the OpenAPI library does not distinguish them. Both produce `Dictionary<string, JsonElement>`.
| `$ref` | Named type reference |
| Nullable (`nullable: true`) | `T?` |

## Generic type monomorphisation

In the OpenAPI spec, generic types are monomorphised — each concrete instantiation becomes its own schema with an `_` delimiter:

- `PagedResult<TaskDto>` → `PagedResult_TaskDto`
- `PagedResult<MemberDto>` → `PagedResult_MemberDto`

In TypeScript, the generic is preserved as `PagedResult<T>` with a type parameter.

When emitted by Rivet, each monomorphised schema carries an `x-rivet-generic` extension. The [importer](/guides/openapi-import) uses this to reconstruct the generic template — so `PagedResult_TaskDto` + `PagedResult_MemberDto` import back as a single `PagedResult<T>` record. See [OpenAPI Round-Trips](/guides/openapi-round-trips) for details.

## Vendor extensions reference

See [Vendor Extensions](/reference/vendor-extensions) for the full `x-rivet-*` extension spec.
