# FABLE_REWRITE_PLAN.md — Phases 1–5 implementation plan

Written 2026-06-11 on `v2`, after FABLE_TEST_FIXES priorities 1–9 landed (rivet `7beb081..`, rivet-ts `1f0ac74..`) and the conformance gate + `FABLE_PHASE0.md` gap analysis stood up. Companion to `FABLE_REWRITE.md` (the why) — this is the how.

## Code-reality findings that shape the plan (read first)

1. **The single most important dependency the rewrite underweights:** rivet-ts's vite plugin invokes the .NET binary as `rivet --from <contract.json> --output <dir>` (`rivet-ts/src/vite.ts:139`) and the scaffolded UI consumes the resulting `rivet.ts`-style client via `emitClientPackage` (`src/infrastructure/codegen/client-package-emitter.ts`, which emits `import * as X from "./rivet/client/<module>.js"` and re-exports `rivetFetch`/`RivetError` from `./rivet/rivet.js`). **Phase 3's deletion of `ClientEmitter`/`Templates/rivet.ts` breaks every rivet-ts daily user** unless the rivet-ts client-generation switch (part of Phase 5) lands first. FABLE_REWRITE sequences Phase 5 as "separate effort" after Phase 3 — code reality says **Phase 5a (client consumption switch) is a hard prerequisite of Phase 3.** This is the one explicit contradiction with the rewrite doc; everything else below follows its leans. Mitigation is cheap: `ensureRivetBinary` pins the binary by release tag, so old rivet-ts releases keep working against old binaries; the constraint is on *release ordering*, not on code coexistence.
2. `x-rivet-*` emission is further along than FABLE_REWRITE assumes. Already emitted by `OpenApiEmitter.cs`: `x-rivet-file` (:262, :695), `x-rivet-query-auth` (:423), `x-rivet-csharp-type` (:713, :756, :1050, :1067), `x-rivet-generic` (:951), `x-rivet-brand` (:966), `x-rivet-empty-record` (:1033). All seven are read back by the importer (`SchemaClassifier.cs:62/:147/:232/:350/:498`, `SchemaMapper.cs:83/:222/:316/:379/:511/:551`, `ContractBuilder.cs:71/:769`). **One asymmetry:** `x-rivet-input-type` is *read* (`ContractBuilder.cs:156`) but never *emitted* — the emit side relies on the `{fieldName}Request` naming convention. Contract/group/endpoint names are carried by convention only (`operationId = "{ControllerName}_{Name}"`, `tags = [UpperFirst(ControllerName)]`, `OpenApiEmitter.cs:161-162`; reversed by `ContractBuilder.DeriveFieldName` :64-65).
3. E8 is half-fixed: `OpenApiEmitter.cs:202` and `ClientEmitter.cs:518/:524` now honour `IsOptional` (wave 3). The remaining half is producer-side: `EndpointWalker` never reads `HasExplicitDefaultValue` (grep-verified zero hits), so C# default-valued params still emit required.
4. Still open, verified by grep/read: **A3** (zero `BaseType` references anywhere in Rivet.Tool), **A5** (`TypeWalker.GetNamespaceGroup` :654 still last-segment-only), **A6** (no `[controller]` handling anywhere), **A7** (`WellKnownTypes.cs:98` resolves only non-generic `ProducesResponseTypeAttribute`), **A8** (mapping table lacks `ProblemHttpResult`/`ValidationProblem`/etc.), **A9** (`TypeWalker.cs:592` `Length >= 2` guard), **A10** (no `FromHeader`/`FromServices` symbols), **A12** (`MapTypeCore`'s `ITypeParameterSymbol` early-return still precedes the `NullableAnnotation` check), **A14** (`ContractWalker.cs:542/:589` JSON-name vs route-name), **R3** (no freeze/seal in `EndpointBuilder`), **E6** (`CollectGenericInstances` :1116 still walks all definitions including generic templates), **E7** (`SchemaEnricher` still writes `$ref` siblings, no `allOf` wrap), **I1** (`SchemaMapper.cs:133/:144` still skips `OpenApiSchemaReference` aliases after registering the name).
5. `OpenApiEmitter` shared-code needs (Phase 3 deletion safety): it depends on `InlineTypeExtractor.CanonicalHash` (:651, :801, :878, :888, :894) and the whole `Model/TsType*` family, plus `SchemaEnricher` and `SecurityConfig`. It does **not** use `TypeGrouper` (grep-verified). `EmitPipeline.RunAsync` runs `InlineTypeExtractor.Extract` *before* any emitter, so extraction is shared infrastructure, not commodity. `JsonSchemaEmitter` is consumed only by the Zod path (`--jsonschema`/`--compile`) and currently emits no `x-rivet-*` at all — it dies with Zod.
6. The .NET tool never *writes* contract JSON in production (no `ContractEmitter.Emit` call sites outside tests) — the format's producers are rivet-ts (lowerer, vite plugin, CLI) and rivet-php; the sole production consumer is `JsonContractReader` behind `--from`. This makes the Phase 5 "internal IR" option concrete and cheap.

---

## Phase 1 remainder — harden the kept half

### WP-1.1 — `x-rivet-*` completion + self-loop losslessness (S)

Inventory (concept → status → action):

| Concept | Emit | Import | Action |
|---|---|---|---|
| queryAuth | `x-rivet-query-auth` :423 | `ContractBuilder.cs:71/:769` | done |
| fileResponse | `x-rivet-file` :262/:695 | `SchemaClassifier.cs:232` | done |
| brands | `x-rivet-brand` :966 | `SchemaClassifier.cs:62/:498` | done |
| csharpType | `x-rivet-csharp-type` ×4 | `SchemaMapper` ×4 | done |
| generics | `x-rivet-generic` :951 | `SchemaClassifier.cs:350`, `SchemaMapper.cs:83` | done |
| empty records | `x-rivet-empty-record` :1033 | `SchemaClassifier.cs:147` | done |
| input record names | **not emitted** | `ContractBuilder.cs:156` reads `x-rivet-input-type` | **emit it** on synthesized request-body schemas (`OpenApiEmitter` request-body construction around :229) |
| contract/group/endpoint names | operationId/tag convention :161-162 | `DeriveFieldName` `ContractBuilder.cs:64-65` | add explicit `x-rivet-contract` + `x-rivet-endpoint` on each operation; keep convention as fallback. Cheap, and makes the self-loop robust against tag/operationId hand-edits |

Oracle: conformance check #3 — `OpenApiRoundTripTests.cs` (flagship `MaximalContract_DoublRoundTrip_IsLossless` :1915) extended to assert name fidelity through tag renames. The `FABLE_PHASE0.md` gap list (GAP-1, GAP-2, W1, W2, W4) folds into this WP.

### WP-1.2 — A-section / R / E gap fills (parallelizable, mostly independent)

Each item lands with its FABLE_TEST_FIXES I.E gap-fill test, test-first:

| Item | Files | Size | Notes |
|---|---|---|---|
| **A3** inheritance | `Rivet.Tool/Analysis/TypeWalker.cs` (~:217 `GetMembers`), `ContractWalker.cs` (:485, :513, :566, :629) | **M** | Walk `BaseType` chains (stop at `object`/`ValueType`/records' synthesized members; dedupe overrides). The highest-value remaining analysis fix; rivet-ts already fixed its mirror X5 in wave 4 — match semantics. |
| A5 collision detection | `TypeWalker.cs:185-194, :654-663` + enum/brand keying :416-441, :454 | S-M | Full-namespace grouping key; collision → loud diagnostic, not silent drop. |
| A6 `[controller]` token | `Analysis/EndpointWalker.cs` `ExtractControllerRoute` :303-320 | S | Substitute `[controller]`/`[action]`. |
| A7 generic ProducesResponseType | `Analysis/WellKnownTypes.cs:98`, `EndpointWalker.cs:600/:631` | S | Resolve `` ProducesResponseTypeAttribute`1 `` too. |
| A8 typed-result table | `WellKnownTypes.cs:154-174`, `EndpointWalker.cs:509-537` | S | Add `ProblemHttpResult`, `ValidationProblem`, `ForbidHttpResult`, `InternalServerError<T>`, `JsonHttpResult<T>`; unmapped branch → diagnostic (per the enforceability rule). |
| A9 Range overload | `TypeWalker.cs:592-599` | S | Guard the `(Type,string,string)` overload; `CultureInfo.InvariantCulture`. |
| A10 param classification | `EndpointWalker.cs:370-395`, `WellKnownTypes.cs` | M | Add `FromHeader`/`FromServices` symbols; `[ApiController]` body inference for complex DTOs; diagnostic for the still-unclassifiable. |
| A12 `T?` on type params | `TypeWalker.cs` `MapTypeCore` (annotation check must precede the `ITypeParameterSymbol` return) | S | |
| A14 JsonPropertyName vs route | `ContractWalker.cs:542/:589, :578-601` | S | Route-bound props keep the route name (or diagnostic on mismatch). |
| R3 builder freeze | `Rivet.Attributes/EndpointBuilder.cs` (all mutators :15-203) | S-M | Throw on mutation after first `Invoke`/publish; test in `FileRouteDefinitionTests`. Runtime-visible — note in release notes. |
| E6 generic-template garbage | `Emit/OpenApiEmitter.cs:1116-1156` | S | Skip definitions with non-empty `TypeParams` when collecting instances (templates emitted as such; only concrete instantiations monomorphise). Fix only the OpenApiEmitter copy — `JsonSchemaEmitter`'s twin dies in Phase 3. |
| E7 `$ref`-sibling enrichment | `Emit/SchemaEnricher.cs:12-78` + call site `OpenApiEmitter.cs:800-804` | S | **Mostly mooted by SYS-1**: OpenAPI 3.1/JSON Schema 2020-12 permits `$ref` siblings. Do the `allOf` wrap only if 3.1 (WP-1.4) is deferred; otherwise fold into WP-1.4 as "verify enrichment survives on `$ref` props post-3.1". |
| E8 remainder | `EndpointWalker.cs:371-395` (set `IsOptional` from `HasExplicitDefaultValue`), `ContractWalker` input-property loop | S | Consumer side already honours `IsOptional` (wave 3). |
| E11 remainder | `OpenApiEmitter.cs:229/:297/:316/:335` | S | `required: false` on request bodies whose type is `TsType.Nullable` (discriminator half already fixed wave 2). |
| **I1** `$ref` alias schemas | `Import/SchemaMapper.cs:95-101, :133, :144, :270-274 (TryResolveSchemaReference)` | S-M | Map alias keys to the *target's* mapped name; add the I.E test (`"Alias": {"$ref": …}` + consumer). Same `WouldGenerateType`-agreement discipline as the wave-3 I2 fix. |

### WP-1.3 — wire conformance checks 1–3 into CI

Gate exists (`OpenApiConformanceTests.cs`). Remaining: a CI job running it + the self-loop on every push; the salvaged tsc harness is check #2's runner (per I.D: fixtures die, harness survives).

### WP-1.4 — SYS-1: `OpenApiEmitter` 3.0.3 → 3.1 (M, atomic)

**Decision: last package of Phase 1, after the gate is green on 3.0.3, before Phase 2 consumers onboard** (so they consume 3.1 from day one and never migrate twice). `openapi-typescript` supports both, so this doesn't block Phase 2 if it slips — but doing it pre-Phase-2 is strictly cheaper. Rationale for not doing it earlier: the gate must exist first so the migration rides a real oracle (FABLE_TEST_FIXES priority 8 says exactly this); rationale for not deferring past Phase 3: it deletes code (`MapNullable` allOf-wrap `OpenApiEmitter.cs:675-700`, `ConvertExclusiveToOpenApi30` :1055-1070) and moots E7, shrinking everything downstream.

Scope:
- `OpenApiEmitter.cs:84` `"3.0.3"` → `"3.1.0"`; nullable → `type: ["T","null"]`; numeric `exclusiveMinimum`/`exclusiveMaximum`; delete the 3.0 downgrade helpers; `example` → `examples` where applicable.
- Importer: already handles 3.1 type arrays (review §3) — verify with the self-loop.
- Test mass-conversion: `OpenApiEmitterTests.cs` (3× `"3.0.3"`, `Emitted_Json_Contains_No_OpenApi31_Nullable_Patterns` :1298-1347 flips from forbid-3.1 to require-3.1, spectral-validated), `FormatRoundTripTests.cs` (5), `GapAnalysisTests.cs` (4), `FromContractTests.cs` (1), `JsonContractEmitterTests.cs` (1), plus every `nullable: true` assertion.
- CLI help text "OpenAPI 3.0 JSON spec" in `CliParser.PrintUsage`.

## Phase 2 — parallel run (S-M, mostly verification labor)

Sample: `samples/ContractApi` (referenced by `SampleProjectTests.cs:19`; boots a real server at :980).

- **WP-2.1**: add a TS consumer alongside the existing generated client — under `Rivet.Tests/js/` or `samples/ContractApi/clientapp/`: `rivet --project ContractApi.csproj --output gen --openapi`, then `openapi-typescript openapi.json -o schema.d.ts`, `openapi-fetch` client, `tsc --strict`. A new `SampleProjectTests` fact drives it; the five existing rivet.ts suites (`:151, :323, :491, :638, :832`) keep running in parallel — they are the comparison baseline and die in Phase 3.
- **WP-2.2**: dual-run the api-responds tests: same booted ContractApi exercised through both clients; assert identical bodies/statuses.
- **WP-2.3**: switch one real app (Max's apps) the same way; live with it. **User task.**
- **Named-method-wrapper decision criteria** (record the answer in FABLE_REWRITE open question 2): (a) call-site ergonomics — `client.GET("/api/tasks/{id}", { params: { path: { id } } })` vs `api.tasks.getTask(id)`; count the diff noise in the real app migration; (b) per-status result discrimination — does `openapi-fetch`'s `{ data, error, response }` shape lose narrowing actually used?; (c) Rivet-specific semantics the generic client can't express: queryAuth token injection (required query param in the spec, so openapi-fetch demands it per call — a wrapper could inject once via config), file/blob endpoints (`x-rivet-file` → wrapper returns `Blob`), brand types (openapi-typescript erases them — wrapper could re-brand). If ≥2 of (a)–(c) hurt in practice, write the wrapper as a small hand-maintained template, not an emitter.

## Phase 3 — deletion (M; the point of no return)

**Gate: Phase 2 sign-off AND WP-5a released in rivet-ts** (see finding 1).

Production deletions in `Rivet.Tool/`:
- Delete outright: `Emit/ClientEmitter.cs`, `Emit/TypeEmitter.cs`, `Emit/ZodValidatorEmitter.cs`, `Emit/JsonSchemaEmitter.cs` (Zod-only consumer), `Emit/TypeGrouper.cs` (verified: only EmitPipeline/TypeEmitter use it), `Emit/ValidateMode.cs`, `Templates/rivet.ts`, `TsReservedWords.cs` (verify importer/`Naming.cs` non-use first).
- **Keep (verified shared):** `Emit/InlineTypeExtractor.cs` (extraction pass in `EmitPipeline.RunAsync` + `CanonicalHash` naming inside `OpenApiEmitter`), all of `Model/`, `Emit/SchemaEnricher.cs`, `Emit/SecurityConfig.cs`, `Emit/OpenApiEmitter.cs`, `Emit/ContractEmitter.cs` + `Emit/JsonContractReader.cs` (fate: Phase 5 — keep, per recommendation below).
- Rewrite `Emit/EmitPipeline.cs`: drop types/client/zod/barrel writing and `PreviewToStdout`'s TS sections; the pipeline becomes extraction → OpenAPI (and `--from` becomes contract-JSON → OpenAPI).
- `Program.cs`/`CliParser.cs` flag surface: **OpenAPI becomes the default output**. Delete `--compile` (and the legacy `zod` token) and `--jsonschema` — with loud "removed in v2; use openapi-typescript" errors, not "unknown flag". Keep `--from`, `--from-openapi`, `--namespace`, `--security`, `--check`, `--routes`, `--quiet`. Update `PrintUsage` and `CliParserTests.cs`.
- Test deletions = FABLE_TEST_FIXES I.D verbatim (P1† ports already done, so this is pure deletion). PHP TS-emission suites re-point at OpenAPI output.
- Docs: `README.md`, `docs/index.md`, `docs/getting-started.md`, `docs/guides/*` rewritten to the meta-framework thesis; add the missing `/reference/cli` page (D2).

## Phase 4 — importer demotion (S; mostly done already)

Waves 1–3 already delivered the loud-diagnostics conversion. Remaining:
- **I1** lands in WP-1.2 (compile-breaker → Phase 1).
- Remaining MED silent-ish drops to markers: I4 (nested allOf middle-layer props, `Import/RecordSynthesizer.cs:41-45`), I5 (`SchemaMapper.ResolveObjectType` :598-602 properties+additionalProperties), I7 (stale fileContentType, `ContractBuilder.cs:267-300`), I8 (top-level `required` on allOf, `RecordSynthesizer.cs:169-172`), I10 (media-type params, `ContractBuilder.cs:612`), I12 (multi-scheme security), I13 (param metadata marker). I4/I8 are fix-properly (inheritance fidelity); the rest are diagnose-loudly.
- **Supported-profile doc**: new `docs/reference/import-profile.md`; reframe `--from-openapi` as "onboarding scaffold" in `CliParser.PrintUsage` and README.
- Conformance check #4: corpora already ratcheted; add "scaffolded C# compiles" to any corpus entry not yet covered.

## Phase 5 — rivet-ts realignment (L; the big one)

### Data-flow map (verified consumers of bespoke contract JSON today)

Producers: rivet-ts lowerer (`typescript-rivet-contract-lowerer.ts`, 2422 lines) via CLI default command, vite plugin (`src/vite.ts:128-134`), scaffold (`generated/api.contract.json`, written since wave 2); rivet-php (`ContractEmitter.php`).

Consumers:
1. **Hono adapter at runtime** — `src/hono.ts:12-30` reads a deliberately loose mirror for route registration + the wave-3 coercion/validation.
2. **Scaffolded app** — `app.ts` imports `generated/api.contract.json` → consumer 1.
3. **Scaffold mock generator** — `mock-value-generator.ts` walks `RivetContractDocument` types.
4. **.NET tool `--from`** — `JsonContractReader.cs` → shared `EmitPipeline`.
5. **rivet-php → `--from`** — same path.

### Decision: contract JSON becomes the **internal IR**; the .NET tool stays the sole OpenAPI emitter (Option B)

Consistent with the rewrite's rivet-php lean ("one OpenAPI emitter to harden, not two") and code reality:
- The Hono adapter's runtime needs map 1:1 onto the current document and only awkwardly onto OpenAPI. Option A (rivet-ts emits OpenAPI 3.1 directly) creates the second emitter the rewrite explicitly doesn't want *and* still needs an IR for the runtime. Option C (runtime consumes OpenAPI) puts spec-parsing in the request path for no benefit.
- The "bug factory" argument was about a *hand-maintained public wire format between independently-versioned repos*. Post-pivot it crosses exactly one boundary — rivet-ts → the .NET binary — which is **version-pinned** (`ensureRivetBinary` keys by release tag), schema-validated (wave 1), and covered by the portable cross-repo conformance test. Keep `rivet-contract-schema.json` versioned (add a `contractVersion` field) but document it as internal.
- **rivet-php: keep internal IR** (per lean) — `rivet:reflect` → contract JSON → `rivet --from`. Its output now surfaces as OpenAPI like everything else.

### WP-5a — client-generation switch (prerequisite of Phase 3) (M)

- `src/vite.ts` `generateArtifacts`: keep writing the contract JSON (runtime + Hono need it); the binary invocation produces `openapi.json`; then run `openapi-typescript` (new dependency) over it and emit an `openapi-fetch`-based client package.
- `client-package-emitter.ts` + `local-rivet-emitter.ts`: rewrite to wrap `openapi-fetch` (`createClient<paths>`); `local.ts`'s injectable-fetch swap (`app.request`) carries over unchanged — `openapi-fetch` accepts a custom `fetch`, so the local-first story survives verbatim.
- `mock-project-emitter.ts`: scaffolded `ui/` imports the new client shape; scaffolded package.json gains `openapi-fetch`. Write `generated/openapi.json` alongside the contract JSON.
- Tests: the scaffold tsc gate and vite real-build core carry over; `rivet-tool-from.lifecycle.test.ts`'s OpenAPI-shape assertions become the primary oracle; II.C wire-format assertion vocabulary dies per that list.

### WP-5b — X13 frontend/lowerer collapse (L, independent, parallelizable with everything)

The **lowerer becomes the single AST→document pass**; the frontend (`typescript-contract-frontend.ts`, 1817 lines) is deleted, its contract-discovery diagnostics (the good half) folded into the lowerer's discovery stage. One shared `ts.Program`, one tsconfig parse, one semantic check. `ExtractTsContracts` use case deleted or reimplemented as a projection of the lowering result; `ContractBundle` and `type-expression.ts` die. Call sites: `src/vite.ts:105-108`, `run-cli.ts:122/:177`, `scaffold-mock-project.ts:39`. Test fallout pre-classified: II.C bundle-IR assertions die wholesale; the wave-4 X-section fixtures re-target the lowerer and must stay green — they are the safety net. Halves the vite plugin's regeneration cost.

### WP-5c — residual rivet-ts items (S)

`rivet-type-to-typescript.ts` codegen: under Option B it loses its client-generation consumer; verify remaining consumers (scaffold mock typing) and prune. Update `docs/guides/*` for the openapi-typescript client.

---

## Ordering / dependency graph

```
[conformance gate — DONE]
        │
Phase 1: WP-1.1 (x-rivet + PHASE0 gaps) ── WP-1.2 (A/R/E/I1 gap fills, parallel) ── WP-1.3 (CI)
        │                                   (all parallel with each other)
        └──▶ WP-1.4 (3.1 migration, atomic, after gate green)
                    │
Phase 2: WP-2.1/2.2 (sample dual-run) ──▶ WP-2.3 (real app, USER) ──▶ wrapper decision
                    │                                              │
Phase 5b (X13 collapse) ─ independent, start anytime ─┐           │
Phase 5a (client switch in rivet-ts) ◀────────────────┴───────────┤  ◀ needs Phase 2's recipe
                    │                                             │
                    ▼                                             ▼
Phase 3 (delete)  ◀── HARD GATE: WP-5a released + Phase 2 sign-off
                    │
Phase 4 (importer profile doc + remaining markers) ─ independent of 2/3/5; anytime after Phase 1
Phase 5c + rivet-php re-point ─ after Phase 3
```

## Risk notes (both repos in daily use)

- **WP-1.4 (3.1)**: emitted-spec consumers must tolerate 3.1. Daily consumers are the rivet-ts vite plugin (doesn't read the OpenAPI today) and any Swagger-UI usage. Low risk pre-Phase-2; near-zero after.
- **WP-1.2 R3 (builder freeze)** is the only runtime-behavior change in Phase 1 — any app that (incorrectly) mutates a published `RouteDefinition` starts throwing. Clear exception message.
- **Phase 3** breaks anyone running `rivet --output` for TS types — every current daily invocation. Dead flags get loud "removed in v2" errors. rivet-ts users are insulated by binary tag pinning, but **do not publish a new binary tag between WP-5a's rivet-ts release and Phase 3** without checking the vite plugin's invocation matches.
- **WP-5a** changes the generated client API surface consumed by scaffolded UIs and real apps — the Phase 2 wrapper decision bounds this migration's pain; sequence WP-2.3 first so the recipe is proven.
- **WP-5b** is large but behavior-preserving by construction; the wave-4 X-fixture suite is the regression net. Own PR; do not mix with WP-5a.
- **rivet-php**: untouched until Phase 3 re-points its round-trip tests; PHP2/PHP3 fixes orthogonal.
