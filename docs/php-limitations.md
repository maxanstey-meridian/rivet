# PHP Reflector — Limitations & Fidelity Gaps

The PHP reflector uses runtime reflection + docblocks instead of a compiler. This means some features available in the C#/Roslyn pipeline have reduced fidelity or are deferred.

## Comparison table

| Concern | C# / Roslyn | PHP / Reflection |
|---|---|---|
| Property types | Compile-time guaranteed | Runtime type hints, not enforced |
| Collection generics | `List<T>` in type system | `array` + docblock `@var` (optional, unenforced) |
| Enum fidelity | Full (string/int, member names, values) | Full for `BackedEnum` (8.1+), docblock unions for older |
| Generic DTOs (e.g. `Page<T>`) | Full support via Roslyn | Requires `@template` docblock — defer to v2 |
| Response types | Method return type or `[ProducesResponseType]` | `#[RivetResponse]` attribute required |
| Validation constraints | `[RivetConstraints]` attribute | Could read Laravel validation rules — defer to v2 |
| Branded types / value objects | `record { Value: T }` pattern | Not applicable — no equivalent PHP idiom |
| Inline objects | C# tuples | Psalm `array{...}` shapes |
| Untyped `array` without `@var` | N/A (compile error) | Emits `unknown[]` + reflector warning |

## Property types

PHP type hints are checked at runtime but not at analysis time — a property declared `public string $name` could still receive `null` if the constructor doesn't enforce it. The reflector trusts the declared type. Static analysers like PHPStan or Psalm can catch mismatches, but that's outside the reflector's scope.

## Collection generics

A bare `array` property without a `@var` docblock emits `unknown[]` with a diagnostic warning. The reflector cannot infer element types from usage.

Always add a docblock when using array properties:

```php
/** @var list<OrderLineItem> */
public array $lines;

/** @var array<string, int> */
public array $quantities;
```

## Generic DTOs

Generic DTO support (e.g. `Page<T>`) is a v2 feature. The `@template` docblock convention exists in Psalm and PHPStan, but the reflector doesn't parse it yet. For now, create concrete classes for each specialisation:

```php
// v1 workaround
class OrderPage {
    public int $page;
    public int $perPage;
    /** @var list<Order> */
    public array $data;
}
```

## Validation constraints

Validation constraint extraction is a v2 feature. Laravel's `$rules` array and Form Request validation could theoretically be read, but this is not yet implemented. For now, validation rules are not reflected into the contract output.

## Branded types / value objects

There is no PHP equivalent of C#'s single-property record pattern used for branded types. PHP value objects are ordinary classes — the reflector has no way to distinguish a "branded wrapper" from any other DTO. These classes are reflected as regular contract types.

## Inline objects

Psalm-style `array{width: int, height: int}` shapes are supported via docblock parsing. The reflector converts these to inline object types in the contract output.

## Deferred to v2

- **Generic DTO support** — parse `@template` docblocks to support `Page<T>`-style patterns
- **Validation constraint extraction** — read Laravel `$rules` arrays and Form Request validation
