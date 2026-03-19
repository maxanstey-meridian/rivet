# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## TypeScript

- Strict mode always. No `any` or `unknown` to silence errors.
- `const` arrow functions, not `function` declarations.
- Fix the type or the model, not the error. No `as any`, no non-null assertions as convenience.
- Prefer narrowing and better modelling over optional chaining everywhere.

## Nuxt / Vue

- `<script setup lang="ts">` always.
- `useState()` with namespace keys for SSR-safe shared state. No Pinia.
- Components are presentational; side effects and orchestration live in pages or composables.
- @nuxt/ui v4 for component library.

## .NET / C#

- `sealed` on all concrete types.
- Records for DTOs, commands, results, value objects.
- Primary constructor DI for use cases.
- Use case signature: `ExecuteAsync(Command, CancellationToken)` — always async, always cancellation token.
- EF Core entities: `sealed class` (not records), `init`/`set`. Fluent config in `*EntityConfiguration.cs`.
- Module registration via `IServiceCollection` extension methods. No auto-scanning.
- FluentValidation with `.WithErrorCode()`. Custom `ValidationActionFilter`.
- Cookie-based auth (HttpOnly `sid`/`rtid`). JWT extracted from cookie.
- `JsonStringEnumConverter` globally.
- Always use braces on control flow bodies — even single-line.
- Never fetch an entity just to check existence — use `ExistsAsync`.
- Colocate `Command` and `Result` records with their use case class.

## Code navigation

Prefer LSP over Grep/Glob/Read:
- `goToDefinition` / `goToImplementation` to jump to source
- `findReferences` to see all usages
- `workspaceSymbol` to find definitions
- `hover` for type info

After writing or editing code, check LSP diagnostics. Fix type errors and missing imports immediately.

## Context

`CONTEXT.md` is your working mental model of this project — post-it notes on a wall. It captures the "why", the gotchas, the decisions, and the cross-cutting knowledge that isn't obvious from reading code or git log.

- **Update it throughout the session** — don't wait until the end. If you learn something non-obvious, write it down.
- **Update in-place** — keep it tight. Don't just append; reorganise, merge, and prune stale entries.
- **Tell me when you update it** — mention "updated CONTEXT.md" in your response so I know.
- **Non-obvious only** — things you can't derive from reading the current code, CLAUDE.md, or git history. No duplication.
- **Remove resolved items** — if future work gets implemented or a gotcha gets fixed, clean it up.

## Rivet architecture — moving parts and cross-cutting concerns

Rivet has several interconnected pipelines. Changes to one often require updates to others. **Before considering a task done, walk this checklist.**

### The two pipelines

1. **C# → TS** (forward): Roslyn reads `[RivetContract]`/`[RivetClient]` classes → `ContractWalker`/`EndpointWalker` → `TsEndpointDefinition` + `TsTypeDefinition` model → emitters (TypeEmitter, ClientEmitter, ValidatorEmitter, OpenApiEmitter)
2. **OpenAPI → C#** (import): JSON spec → `OpenApiImporter` → `SchemaMapper` + `ContractBuilder` → `CSharpWriter` → generated `.cs` files that feed back into pipeline 1

### Contract style

Contracts are `[RivetContract] public static class` with `EndpointBuilder<T>` fields. **Not** abstract classes, **not** ASP.NET-coupled. The `EndpointBuilder<T>.Invoke()` method provides type-safe runtime execution. `RivetResult<T>` is framework-agnostic; the consumer provides a `ToActionResult()` bridge.

ASP.NET name collisions: generated contracts must include `using Endpoint = Rivet.Endpoint;` and `using EndpointBuilder = Rivet.EndpointBuilder;` aliases.

### What to check after changes

**If you change `Rivet.Attributes` (Endpoint, EndpointBuilder, RivetResult, attributes):**
- `ContractWalker` — reads these types via Roslyn. Field type filter must match.
- `EndpointWalker` — reads `[RivetClient]` classes, shares code with ContractWalker.
- `OpenApiEmitter` — emits from the model, not directly from attributes, but verify tests.
- `CSharpWriter` — generates code that uses these types. Must stay in sync.
- `samples/ContractApi/` — must build and demonstrate current patterns.
- Run **all** tests, not just the ones for the file you changed.

**If you change the importer (`Rivet.Tool/Import/`):**
- Fixture round-trip tests — generated C# must compile AND survive `ContractWalker` → endpoints.
- `samples/ContractApi/` — should reflect what the importer would generate.
- Drift detection tests — verify static class output, typed builder fields, no abstract class patterns.

**If you change `ContractWalker` or `EndpointWalker`:**
- `OpenApiEmitterTests` — all use contracts/endpoints via walkers.
- `ContractEndpointTests` — tests static class contracts and (legacy) abstract class contracts.
- Importer round-trip tests — generated contracts must be walkable.

**If you change the OpenAPI emitter:**
- Forward direction: C# contracts → OpenAPI JSON. Test with `OpenApiEmitterTests`.
- The importer is the reverse direction and is independent, but the type mappings should be consistent (what the emitter outputs, the importer should be able to consume).

**If you change type mappings (SchemaMapper, TypeWalker, TsType):**
- Both directions must stay consistent. If you add a type mapping in SchemaMapper (OpenAPI → C#), check whether the reverse mapping exists in OpenApiEmitter (C# → OpenAPI).
- `Rivet.Tests/Fixtures/openapi-import.json` is the comprehensive fixture — it should exercise every supported type.

### Staleness hotspots

These are the things that have gone stale before:
- `Rivet.slnx` — project references (deleted projects left behind).
- `samples/ContractApi/` — contract style drifted from what the importer generates.
- `README.md` — feature docs lagging behind implementation.
- Importer test assertions — checking string patterns that don't match the current output format.

### Testing requirements

- **Always add tests for new functionality.** No exceptions.
- **Fixture round-trip tests are mandatory** for importer changes: OpenAPI JSON → import → compile → Roslyn walker → verify endpoints + types survive.
- **Run the full test suite** (`dotnet test`) before declaring done, not just filtered tests.
- **Build the samples** (`dotnet build samples/ContractApi/ContractApi.csproj`) — they catch real-world issues (name collisions, missing usings) that unit tests miss.

## How to work with me

- **Default mode is rubber-ducking.** No code changes unless I give an imperative instruction.
- Treat me as senior. Conclusions first, then reasoning. No preamble.
- Challenge my assumptions with evidence.
- When I ask "is this clean?" — evaluate boundaries, type story, hidden dependencies.

## Debugging and refactoring

- **No shims.** Root causes, not symptoms.
- **Zoom out.** Is this a local fix or a broader design issue?
- **Respect invariants.** Do not weaken domain invariants to make something compile.

## Hard rules

1. **Principle-first answers.** Lead with one unqualified recommendation from first principles, not "for this codebase today".
2. **95% confidence threshold.** If not ~95% confident, say so and explain what would firm it up.
3. **No invented commands.** Infer tooling from project files.
4. **Proven solutions before hand-rolling.** Before implementing infrastructure concerns (auth, email, file storage, job scheduling, etc.), evaluate whether a battle-tested library or framework already handles it. The same principle as "every package must earn its place" applies in reverse: every hand-rolled solution must justify itself over an existing, audited alternative. Flag the option and the trade-offs before writing custom code.
