# FABLE_REWRITE.md — the meta-framework pivot

Written 2026-06-10, following the reviews in `FABLE_REVIEW.md` (this repo) and `rivet-ts/FABLE_REVIEW.md`. This document supersedes the *priorities* of those reviews, not their findings — every finding is still true; this changes which ones are worth fixing versus deleting. A triage table is below.

## The thesis

Rivet stops being a codegen suite and becomes a meta-framework:

> **Rivet owns what nothing else does: contracts authored in C# next to your handlers, extracted by Roslyn, enforced at runtime by the router and `TypedResultValidator`. It speaks to everything else through standards-grade OpenAPI 3.1.** Everything downstream — TS clients, Zod validators, docs UIs, mock servers, other languages — is the OpenAPI ecosystem's job (`openapi-typescript`, `openapi-fetch`, `openapi-zod-client`, Kiota, …). Everything upstream — existing specs, Smithy models (via `smithy-cli`'s OpenAPI converter), TypeSpec (emits OpenAPI natively) — enters through a **one-shot** import.

### Why

1. **The emit half of Rivet competes with better-resourced free tools.** `openapi-typescript` already handles the full OpenAPI surface — including every construct our importer and emitters have verified bugs on (`allOf` idioms, discriminators, `$ref` aliases, form encoding). Owning a TS type-system bridge is a permanent long-tail maintenance tax with a bus factor of one.
2. **The differentiated half has no competitor.** TypeSpec/Smithy are spec-first: contracts in a separate IDL, conformance by generate-stubs-and-hope, no runtime enforcement. Rivet's contract is the routing table and is validated at runtime — it cannot drift by construction. That is the product.
3. **The bespoke contract JSON as a public interchange format is a bug factory.** The entire `optional`/`isOptional` class of cross-repo bugs (rivet-ts review N1–N3, N5) exists because two repos hand-maintain a private schema. OpenAPI has a published spec, ecosystem validators, and a community keeping it honest. The contract JSON may survive as an *internal* IR; it stops being a wire format.

### The new pipeline

```
[RivetContract] C# ──Roslyn──▶ OpenAPI 3.1 (+ x-rivet-* extensions) ──ecosystem──▶ TS types/client/Zod/docs/mocks
        │
        └────────▶ runtime: RouteDefinition routing + TypedResultValidator (unchanged)

one-shot onboarding: any OpenAPI spec ──importer──▶ scaffolded C# contracts (then the spec goes in the bin)
```

### Decisions locked in by this document

- **No round-tripping.** Import is one-shot scaffolding. After import, C# is the sole source of truth and OpenAPI is regenerated output, never read back. This deletes the escrow/merge/refactoring-drift problem class entirely.
- **`x-rivet-*` vendor extensions** carry Rivet-only semantics in the emitted OpenAPI (`queryAuth`, `fileResponse`, brand types, `csharpType` hints, contract/group/endpoint names) so the emitted spec is lossless *for our own concepts*. Standard practice (cf. `x-ms-*`, Smithy traits).
- **The enforceability test** decides DSL scope: if `TypedResultValidator` could enforce a construct, it deserves a DSL form; if not, the importer drops it with a loud, named diagnostic. No silent drops, no escrowed blobs the runtime can't see. The importer ships a documented supported profile.
- **No native Smithy/TypeSpec frontends.** Both convert to OpenAPI with their own maintainers' tools; that is the ingestion path.

## What stays, what goes

### Stays (the product)

| Component | New status |
|---|---|
| `Rivet.Attributes` (DSL + runtime) | Unchanged role. Runtime bug fixes from review still required (R1, A1 status divergence). |
| `ContractWalker` / `EndpointWalker` / `TypeWalker` | Unchanged role. Analysis findings (A-section) all still live. |
| `OpenApiEmitter` | **Promoted to load-bearing.** Its fidelity now determines client correctness. Gains `x-rivet-*` emission and a conformance gate. |
| `TypedResultValidator` | Unchanged — it's the thesis. |
| `OpenApiImporter` / `SchemaMapper` / `ContractBuilder` / `CSharpWriter` | **Demoted to one-shot scaffolding.** Compile-breakers (I1/I2/I3) still worth fixing — scaffold output must compile — but the supported profile narrows and bugs become annoyances, not pipeline corruption. |
| rivet-php | Same treatment as the C# frontend: a reflector whose output ultimately surfaces as OpenAPI. (Open question below.) |
| rivet-ts: extraction frontend, Hono runtime, scaffold | All survive — see cross-repo section. |

### Goes (the commodity middle)

| Component | Replaced by |
|---|---|
| `ClientEmitter` + `Templates/rivet.ts` | `openapi-typescript` + `openapi-fetch` (optionally a thin hand-written named-method wrapper for the `api.users.getUser(...)` ergonomics and per-status result discrimination — a preference we own cheaply, not infrastructure) |
| `TypeEmitter` (TS types) | `openapi-typescript` |
| Zod emitter | `openapi-zod-client` (if runtime client validation still wanted) |
| Contract JSON as public interchange format | OpenAPI 3.1. Contract JSON becomes internal IR or is removed. |

## Finding triage — what the pivot does to the two FABLE_REVIEWs

### rivet (.NET) review

- **A1–A19 (analysis): all still live.** ContractWalker survives unchanged. A1 (DELETE 204/200 divergence + `TypedResultValidator` NoContent throw) and A3 (inherited properties dropped) remain top fixes.
- **E-section (emit): mostly superseded.** E1 (form-encoded client breakage), E2 (lossy Zod names), and everything specific to `ClientEmitter`/`TypeEmitter`/Zod/`rivet.ts` dies with the deleted code. **Exception: every `OpenApiEmitter` finding is promoted, not superseded** — that file is now the most important emitter in the repo.
- **I-section (import): demoted, not superseded.** Fix the compile-breakers (I1/I2 dangling names, I3 input collisions); narrow the profile; convert remaining silent skips to diagnostics. Stop investing beyond that.
- **R-section (attributes), C-section (CLI), P-section (packaging): all still live** (R1 `SingleOrDefault` landmine, C1/C2 single-file binary modes, etc.).
- **PHP-section: still live**, shape depends on the rivet-php open question below.
- **T-section (tests): reshaped** — the centerpiece becomes the conformance gate (below), which replaces several of the suggested fixture-level tests.

### rivet-ts review

- **N-section (interop): superseded as a class.** N1/N2/N3/N5 (`optional`/`isOptional`, schema validation failure, dropped `queryAuth`, `properties`+`type`) all exist because of the bespoke contract JSON wire format. When rivet-ts speaks OpenAPI, the category disappears. **Exception: N4 (`SuccessStatus` types POST as 200) is a runtime-types bug and still live.**
- **H-section (Hono runtime): all still live** — the type/runtime honesty gaps (H1/H2/H3) are unaffected by the pivot.
- **S-section (scaffold): all still live**; S3 (contract JSON never written) eventually becomes "OpenAPI never written."
- **X-section (extraction): all still live.** The frontend/lowerer survive; the X13 collapse recommendation stands and gets easier (one pass, lowering to OpenAPI or to the internal IR).
- **P1 (`export type` crash), C-section (CLI), D-section (docs): all still live.**

## The conformance gate (the new test centerpiece)

All off-the-shelf; this replaces owning correctness checks by hand:

1. **Lint:** `spectral lint` (and/or `redocly lint`) over every emitted spec — zero errors.
2. **Consume:** run `openapi-typescript` over every emitted spec, then `tsc --strict` the output — zero errors. This is the "if the OpenAPI is wrong, the free client is wrong" oracle.
3. **Self-loop:** `import(emit(contract))` produces a semantically identical contract for every fixture in the repo (achievable perfectly once `x-rivet-*` lands, since both ends are ours).
4. **Importer stability:** for the foreign-spec corpus (petstore, GitHub, Stripe, …): import succeeds or diagnoses loudly — never silently drops — and the scaffolded C# compiles. (No `emit ≈ input` requirement: import is a projection by design.)

## Migration plan — strangler, not big-bang

This is mostly deletion; the new code is small (extension emission, conformance tests, optionally a thin client wrapper). Rivet is in daily active use — do not break the working pipeline before its replacement is proven.

- **Phase 0 — the five-minute experiment.** Run spectral + `openapi-typescript` + `tsc` over the *current* `OpenApiEmitter` output for the existing fixtures and the ContractApi sample. The error list is the real gap analysis for everything below; it may be shorter than feared.
- **Phase 1 — harden the kept half.** Fix the still-live analysis/runtime bugs (A1, A3, R1 first). Bring `OpenApiEmitter` to conformance-gate green. Add `x-rivet-*` emission + importer read-back; wire conformance checks 1–3 into CI.
- **Phase 2 — parallel run.** Switch one real consumer (the sample app, then your apps) to `openapi-typescript`/`openapi-fetch` against the emitted spec, with the old generated client still available. Decide here whether the named-method wrapper is worth writing.
- **Phase 3 — delete.** Remove `ClientEmitter`, `TypeEmitter`, Zod emitter, `rivet.ts` template and their tests. Update docs to the meta-framework thesis. (This is the point of no return; everything before it is reversible.)
- **Phase 4 — importer demotion.** Fix I1/I2/I3, write the supported-profile doc, convert silent skips to diagnostics, add conformance check 4. Mark the feature "onboarding scaffold."
- **Phase 5 — rivet-ts realignment** (separate effort): scaffold writes/consumes OpenAPI instead of contract JSON; X13 frontend/lowerer collapse; the still-live H/S/P1 fixes from its review. The Hono local-first story is unchanged in spirit — it just speaks the standard format.

## Open questions — resolutions (2026-06-11, executed on `v2`; see FABLE_REWRITE_PLAN.md for rationale)

1. **rivet-php: keeps producing the internal IR.** `rivet:reflect` → contract JSON → `rivet --from` → OpenAPI. One emitter to harden. Its round-trip tests were re-pointed at OpenAPI output in Phase 3.
2. **Named-method client wrapper: not justified** (WP-2.3 executed against ~/Sites/golden, see FABLE_PHASE0.md "Golden migration"). Ergonomics improved (net −10 lines in the UI layer), error typing improved (exact `ErrorResponse` instead of an `unknown` catch-all arm), per-status discrimination redundant under the single-envelope doctrine. Residual wrapper case: queryAuth/file/brand consumers only — revisit if such an app migrates and hurts.
3. **Zod: dropped.** Server-side enforcement is `TypedResultValidator` + the Hono adapter's runtime coercion/400s; client-side validation, if ever wanted, is `openapi-zod-client` over the emitted spec (documented in both repos).
4. **Contract JSON: internal IR.** It crosses exactly one boundary (rivet-ts/rivet-php → the version-pinned .NET binary), is schema-validated in tests, and maps 1:1 onto the Hono runtime's needs. `rivet-contract-schema.json` stays versioned but documented as internal; OpenAPI 3.1 is the sole public format.

All five migration phases were executed on `v2` (waves logged in git history): Phase 0–1 (conformance gate green on 3.1, x-rivet complete, A/R/E/I gap fills), Phase 2 (openapi-fetch dual-run: zero divergences), Phase 3 (commodity emitters deleted, −9,269 lines), Phase 4 (importer demoted, profile documented), Phase 5 (X13 collapse, openapi-fetch client switch, Zod dropped).
