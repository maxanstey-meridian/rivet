# Type Mapping

## C# → TypeScript → OpenAPI

| C# | TypeScript | OpenAPI JSON Schema |
|---|---|---|
| `string`, `Guid` | `string` | `type: string` |
| `int`, `long`, `decimal`, `double`, `uint`, `ulong` | `number` | `type: number` |
| `bool` | `boolean` | `type: boolean` |
| `DateTime`, `DateTimeOffset`, `DateOnly` | `string` | `type: string` |
| `T?` (nullable value/ref) | `T \| null` | `type: ["<base>", "null"]` |
| `List<T>`, `T[]`, `IEnumerable<T>`, `IReadOnlyList<T>` | `T[]` | `type: array, items: {...}` |
| `Dictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `Record<string, T>` | `type: object, additionalProperties: {...}` |
| `sealed record` | `type { ... }` (transitive discovery) | `type: object, properties: {...}` |
| `enum` (with `JsonStringEnumConverter`) | `type Status = "A" \| "B"` | `type: string, enum: [...]` |
| `PagedResult<T>` (generic record) | `PagedResult<T>` | Monomorphised: `PagedResultOfT` |
| `JsonElement`, `JsonNode` | `unknown` | `{}` |
| `JsonObject` | `Record<string, unknown>` | `type: object` |
| `JsonArray` | `unknown[]` | `type: array` |
| `Email(string Value)` (single-property VO) | `string & { readonly __brand: "Email" }` | `type: string` (brand unwrapped) |

::: info Note
The OpenAPI emitter maps from the TypeScript type model, which does not carry `format` or `integer` vs `number` distinctions. All numeric C# types emit as `type: number`, all string-like types emit as `type: string`, and branded value objects are unwrapped to their inner primitive. The [importer](/guides/openapi-import) does use `format` fields when reading specs — the asymmetry is intentional (richer input, simpler output).
:::

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
| `string` + `format: guid` / `uuid` | `Guid` |
| `string` + `format: email`, `uri`, etc. | Branded value object |
| `string` + `enum: [...]` | `enum` |
| `integer` / `integer` + `format: int32` | `int` |
| `integer` + `format: int64` | `long` |
| `number` / `number` + `format: double` | `double` |
| `number` + `format: float` | `float` |
| `boolean` | `bool` |
| `array` + `items` | `List<T>` |
| `object` + `properties` | `sealed record` |
| `object` + `additionalProperties` | `Dictionary<string, T>` |
| `$ref` | Named type reference |
| Nullable (`type: ["string", "null"]`) | `T?` |

## Generic type monomorphisation

In the OpenAPI spec, generic types are monomorphised — each concrete instantiation becomes its own schema. For example:

- `PagedResult<TaskDto>` → `PagedResultOfTaskDto`
- `PagedResult<MemberDto>` → `PagedResultOfMemberDto`

In TypeScript, the generic is preserved as `PagedResult<T>` with a type parameter.
