# Contributing to Rivet

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — `global.json` pins 10.0.100 and rolls forward to later feature bands.
- .NET 8 and 9 runtimes — `Rivet.RuntimeTests` runs on net8.0, net9.0 and net10.0; the tool and most samples target net9.0.
- Node.js and [pnpm](https://pnpm.io/installation) — the repo pins pnpm 10.24.0 through `packageManager`.
- [go-task](https://taskfile.dev/installation/) (`task`).
- Python 3 — `task test` and `task test:functions` run Python checks.
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local) (`func`) — only for the Functions sample and `task test:functions`.

## Quick start

```bash
git clone https://github.com/maxanstey-meridian/rivet.git
cd rivet
task install   # dotnet tool restore + pnpm install (root, docs, Rivet.Tests/js)
dotnet build
dotnet test
dotnet build samples/ContractApi/ContractApi.csproj
```

Run `task install` before the first `dotnet test`. The test suite shells out to the node tooling vendored
in `Rivet.Tests/js` (spectral, openapi-typescript, openapi-fetch, tsc); without it about 30 tests fail
with "Node tool not found" or "openapi-fetch not installed".

The samples project catches real-world issues (name collisions, missing usings) that unit tests miss.

### Tasks

| Task                  | Runs                                                                              |
|-----------------------|-----------------------------------------------------------------------------------|
| `task install`        | `dotnet tool restore` and every `pnpm install` the build and tests need           |
| `task check`          | Build, full test suite, every sample, docs build                                  |
| `task format`         | Analyzer fixes, then CSharpier                                                    |
| `task format:check`   | The same, verify-only                                                             |
| `task test:functions` | The Functions sample against a real Azure Functions host (Core Tools v4 required) |
| `task ci`             | `task check` plus `task test:functions`                                           |

Run `task check` and `task format:check` before opening a PR. There is no PR CI yet, so these are the gate.

## Architecture at a glance

Rivet has two pipelines:

**Forward (C# → OpenAPI):**
Roslyn reads attributed classes → walkers build an intermediate model → the OpenAPI emitter produces the 3.1 spec.

```
[RivetContract] / [RivetClient]
  → ContractWalker / EndpointWalker
  → TsEndpointDefinition + TsTypeDefinition
  → InlineTypeExtractor → OpenApiEmitter → openapi.json
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

Contracts are `[RivetContract] public static class` declarations with typed route-definition fields — not abstract
classes. Application execution remains ordinary C#; closed `RivetResult` values are created only by contract-owned
terminals and adapted to MVC or Minimal APIs at the transport boundary.

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
- Declare string enums with a type-level `JsonConverter` attribute (the built-in converter for exact member names, or a Rivet naming-policy converter). Unannotated enums are numeric. Rivet cannot infer arbitrary global serializer settings.

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
