# Contributing to Rivet

## Quick start

```bash
git clone https://github.com/user/rivet.git
cd rivet
dotnet build
dotnet test
dotnet build samples/ContractApi/ContractApi.csproj
```

The samples project catches real-world issues (name collisions, missing usings) that unit tests miss.

## Architecture at a glance

Rivet has two pipelines:

**Forward (C# → TS):**
Roslyn reads attributed classes → walkers build an intermediate model → emitters produce TS types, clients, validators, and OpenAPI specs.

```
[RivetContract] / [RivetClient]
  → ContractWalker / EndpointWalker
  → TsEndpointDefinition + TsTypeDefinition
  → TypeEmitter, ClientEmitter, ZodValidatorEmitter, OpenApiEmitter
```

**Import (OpenAPI → C#):**
JSON spec comes in, generated `.cs` files come out — and feed back into the forward pipeline.

```
OpenAPI JSON
  → OpenApiImporter
  → SchemaMapper + ContractBuilder
  → CSharpWriter
  → .cs files (consumed by forward pipeline)
```

### Contract style

Contracts are `[RivetContract] public static class` with `EndpointBuilder<T>` fields — not abstract classes, not
ASP.NET-coupled. `RivetResult<T>` is framework-agnostic.

## Code style

These are non-negotiable:

- **Data transforms over object hierarchies.** Plain types, sealed records, object spread, pure functions. Not entities
  with methods, not inheritance.
- **Minimal dependencies.** Every package must earn its place.
- **Readability over abstraction.** Three similar lines beats a premature helper. Procedural code that reads
  top-to-bottom beats clever composition.
- **Let patterns prove themselves** before extracting. Ship working code, then tighten.

## C# conventions

- `sealed` on all concrete types.
- Records for DTOs, commands, results, value objects.
- Colocate `Command` and `Result` records with their use case class.
- `JsonStringEnumConverter` globally.

## TypeScript conventions

- Strict mode always.
- `const` arrow functions, not `function` declarations.
- Fix the type or the model, not the error. No `as any`, no non-null assertions as convenience.
- Prefer narrowing and better modelling over optional chaining everywhere.
- Do not use `?.` or `??` just to silence errors — ask whether the type is wrong or the code is wrong.

## Type safety

This applies across all stacks:

- **No `object`, `object?`, `dynamic`, `any`, or `unknown` as value carriers.** Model the actual type. If a value can be
  string, bool, or number — make a discriminated union, not `object?`.
- **`Dictionary` only for truly dynamic data** — user-provided key-value pairs, JSON blobs from external APIs. If the
  keys are known at compile time, use a record/type.
- Every `object?` or untyped dictionary is a signal to model better, not to weaken the type.

## Testing

- **Always add tests for new functionality.** No exceptions.
- **Fixture round-trip tests are mandatory** for importer changes: OpenAPI JSON → import → compile → Roslyn walker →
  verify endpoints + types survive.
- **Run the full test suite** (`dotnet test`) before declaring done, not just filtered tests.
- **Build the samples** (`dotnet build samples/ContractApi/ContractApi.csproj`) — they catch issues unit tests miss.

## Cross-cutting checklist

Before considering a change done, check whether it touches adjacent components:

| If you changed…                                             | Also verify…                                                                                    |
|-------------------------------------------------------------|-------------------------------------------------------------------------------------------------|
| `Rivet.Attributes` (Endpoint, EndpointBuilder, RivetResult) | `ContractWalker`, `EndpointWalker`, `CSharpWriter`, `samples/`, all tests                       |
| Importer (`Rivet.Tool/Import/`)                             | Fixture round-trip tests, `samples/`, drift detection tests                                     |
| `ContractWalker` or `EndpointWalker`                        | `OpenApiEmitterTests`, `ContractEndpointTests`, importer round-trip tests                       |
| OpenAPI emitter                                             | `OpenApiEmitterTests`, type mapping consistency with importer                                   |
| Type mappings (SchemaMapper, TypeWalker, TsType)            | Both directions must stay consistent — if you add a mapping in one direction, check the reverse |

## PR expectations

- Tests pass. All of them, not just the ones you touched.
- Samples build.
- No shims. Root causes, not symptoms. Don't weaken domain invariants to make something compile.
- If you're adding a type mapping, it works in both pipeline directions.
- Keep the diff focused. Don't refactor surrounding code unless it's directly necessary.
