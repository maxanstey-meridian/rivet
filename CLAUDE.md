# CLAUDE.md

## What Rivet is (v2)

A meta-framework over OpenAPI: Roslyn reads compiled C# (contracts, controllers,
minimal APIs) and emits an OpenAPI 3.1 spec; codegen (TS types, clients, Zod) is
delegated to the OpenAPI ecosystem. The v1 TypeScript/Zod emitters (`--compile`,
`--jsonschema`) are gone — OpenAPI is the only public output. The contract JSON
consumed by `--from` is an internal IR shared with the sibling runtimes
(rivet-ts, rivet-php), not a public format.

Pipeline (`Rivet.Tool/`):

- `Analysis/` — `TypeWalker` (C# types → `TsType` model), `ContractWalker`
  (`[RivetContract]` `Define.*` chains), `EndpointWalker` (controllers/minimal
  APIs), `CoverageChecker` (`--check`).
- `Emit/` — `EmitPipeline` → `OpenApiEmitter` (spec + `x-rivet-*` extensions);
  `JsonContractReader` for `--from`; `OpenApiDocumentInfo` for
  `--title`/`--version`/`--server`.
- `Import/` — `--from-openapi` one-shot onboarding scaffold: `OpenApiImporter` →
  `SchemaMapper`/`ContractBuilder` → `CSharpWriter` (generates C# that feeds the
  forward pipeline).
- `Diagnostics.cs` — the registry of stable `RIVnnnn` IDs (1xxx extraction,
  2xxx emission, 3xxx import, 4xxx coverage). Retired IDs are never reused.

`Rivet.Attributes/` is the runtime-facing package: attributes, the
`Define`/`RouteDefinition` builder, `Invoke` helpers. Runtime enforcement is
deliberately narrow (status codes + C# payload types on the typed-results path;
`[RivetConstraints]` is a `ValidationAttribute` enforced by validating hosts) —
`docs/guides/runtime-validation.md` is the scope statement.

## Repo layout

`Rivet.Attributes/`, `Rivet.Tool/`, `Rivet.Tests/`, `samples/` (ContractApi is
the contract + runtime-enforcement exemplar; AnnotationApi, TypeShowcase,
ImportDemo), `docs/` (VitePress, deployed to GitHub Pages), `openapi/` (import
corpus fixtures), Taskfile.yml. (The PHP lowerer lives in its own repo,
`rivet-php`.)

## Build / test

- `task build` / `task test` / `task samples:build` / `task check` (everything,
  incl. docs build) — or directly: `dotnet test ./Rivet.Tests/Rivet.Tests.csproj`.
- Single area: `dotnet test --filter <TestClassName>`.
- Conformance tests shell out to spectral in `Rivet.Tests/js/` (`npm install
  --prefix Rivet.Tests/js` once, or `task install`).
- Before done: full `dotnet test` + sample builds (`task samples:build`).

## Test conventions

- Test-first; every behavior change lands with a pinning test.
- **Conformance gate** (`OpenApiConformanceTests`): every emitted fixture spec
  must pass spectral lint (zero errors), parse as 3.1, and satisfy importer
  stability — don't weaken the ruleset to pass.
- **Ratchets** (`ImportMetricTests`): import warnings must fall into named
  categories keyed by RIV ID (`CategorizeWarning`); unsupported counts may only
  go down. New categories are added consciously, never absorbed.
- **Diagnostics**: every stderr warning carries a stable RIV ID.
  `DiagnosticsTests` cross-checks `Rivet.Tool/Diagnostics.cs` against
  `docs/reference/diagnostics.md` in both directions — adding/retiring an ID
  requires editing that page in the same change.
- **Round-trips** (`OpenApiRoundTripTests`): emit → import → emit must reach a
  fixed point; importer changes need fixture round-trip coverage.
- Real-world import corpus lives under `openapi/`; scaffolded C# must compile
  (`ImportMetricTests.Scaffolded_CSharp_Compiles`, `RealWorldImportTests`).

## Docs

`docs/` claims are verified-against-code; when behavior changes, update the
matching reference page (diagnostics.md is test-enforced, the rest is
discipline). `README.md` feature claims and `docs/misc/limitations.md` go stale
easily — check them when adding capabilities.
