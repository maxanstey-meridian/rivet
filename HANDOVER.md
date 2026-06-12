# HANDOVER — Rivet / rivet-ts / Meridian tooling

**Date:** 2026-06-12. **Scope:** `~/Sites/medway/rivet` (branch `v2`), `~/Sites/medway/rivet-ts` (branch `scaffolder` — the release candidate, superset of its `v2`), `~/.meridian` (plumb on branch `v8`, `bin/meridian`), `~/Sites/golden` (.NET exemplar), `~/Sites/golden-meridian` (one generated showcase per flavor).

Most detail is deliberately NOT here — it lives where it's verifiable. This doc says what's true, what's decided, and what's next; for the how, read the commit messages (they're written to be read) and the docs listed at the bottom.

## What we did, and why

**The v2 pivot (rivet):** the v1 C#→TS client emitters are gone. Rivet's job is now *contracts → openapi.json*; TS clients come from the OpenAPI ecosystem (openapi-typescript + openapi-fetch). Everything below serves one promise: **nothing is dropped silently, and the spec and the runtime tell the same story.**

**Capability review → fix waves (2026-06-11, all on `v2`):** `FABLE_GAPS.md` is the audited gap register. Since it was written: P0 promise-breakers fixed (`d69cb08`), all of P2 expressiveness shipped in five waves (`d693a5e..9701370`: info/servers flags, RIV diagnostic IDs, dict key types + multipart lists, `[JsonPolymorphic]`→oneOf both directions, headers as contract concepts), inbound constraint enforcement shipped (`cd5caba` + golden `9f39dae` + rivet-ts `74a9b33`), docs honesty passes (`51259c8`, `c4b51f9`).

**Scaffolder rebuild (2026-06-11, rivet-ts `scaffolder`):** `rivet-ts scaffold|scaffold-mock` emit the golden monorepo shape (Hono api with PA/VSA modules + Nuxt 4 SPA + contracts package), suffix-free, **plumb-clean by construction** — the lifecycle tests gate on shape → tsc → runtime → plumb-zero. `meridian init <dir> [--ts-backend|--dotnet-backend|--no-api]` wraps it (scaffold → git init → plumb). The "proper scaffold" package (`81ba30e`): Zod 4 edge validation compile-locked to the contract with `satisfies`, the ui's `UForm` consuming the SAME schemas via the api's `./validation` export, Dexie browser persistence vs in-memory server adapter behind one port, a `users` module (current-user port + dev stub), logger/CORS on the server entry only.

**Disk-truth fixes (2026-06-12, rivet `0cc45e3..083db9c`):** a human-eye corpus review found the in-memory test suite green while the real CLI pipeline failed on 3/3 sampled specs. Root cause fixed first: `CliPipelineTests` is a new e2e gate that runs the actual CLI over notion/github/cloudflare — import → disk → loose-file compile → re-emit → resolve every `$ref`. Then the bugs it exposed, in order: full shared-framework references for loose-file compile; reserved member renames (`equals`→`EqualsValue`) **plus wire-name pinning** (`user_id` no longer silently drifts to `userId` — every snake_case property was wrong before); case-insensitive name registries + directory CLI args (11k paths overflow ARG_MAX); embedded example-`$ref` values inlined at import; `[RivetUnion]` so undiscriminated `oneOf` round-trips faithfully *and* serializes as the bare variant at runtime (the attribute is its own JsonConverter). Suite: 1242, including the gate.

**Scaffolder fix (2026-06-12, rivet-ts `d88d4f2`):** emitted type imports used the contract's brand string instead of the exported interface identifier — broke exactly the doctrine-conventional `XContract`/`"X"` naming. Lifecycle fixtures now split the two names so the gate covers the class permanently.

## Decisions (do not re-litigate)

- **Types-only TS clients.** No Zod client generation; openapi-zod-client is opt-in only.
- **Hono outbound response stripping: deprioritized.** "As soon as it gets to just-MVP it's a TS backend."
- **rivet-ts richness parity with .NET: deferred** — rivet-ts is a plaything.
- **D1 (2026-06-12): TS scaffold goes module-local**, mirroring golden .NET — `src/modules/quotes/quotes-routes.ts` (↔ `NotesEndpoints.cs`) + `quotes.module.ts` (↔ `NotesModule.cs`); top-level `src/interface/` dies. **SHIPPED same day** (rivet-ts `efc8c59`, tag `v0.11.1`; doctrine = FABLE_CONTRACT §9.10; `.module.ts` only where the module has wiring to own — scaffold-mock emits none).
- **D2 (2026-06-12): undiscriminated oneOf re-emits from the As\* wrappers** — shipped (`3f183b0`).
- **Observability is edge-only** (plumb FABLE_CONTRACT §9.9, prose-only rule).
- File naming is suffix-free (§9.1, `.handler.ts` included); HTTP registration filename is `<module>-routes.ts`.

## Claimed unsupported / degraded-by-design

Authoritative list: `docs/reference/import-profile.md` (kept honest by tests). Highlights: HEAD/OPTIONS/TRACE dropped with named warnings; cookie params location-erased with markers; `{"type":"null"}` oneOf variants degrade to a permissive `{}`; constraints on params don't survive into synthesized input records (marker per param); reserved-name *params* are renamed with a loud marker (they bind by member name — pinning is impossible).

## Done since this doc was written (2026-06-12, same day)

1. **The release act — DONE.** rivet 0.35.0 shipped off `v2` (tag `v0.35.0`: GitHub release binaries ×4 + NuGet `Rivet.Attributes`/`dotnet-rivet`). Two CI gaps fixed en route: `publish.yml` now `npm ci`s `Rivet.Tests/js`, and `CliPipelineTests` is `[Category=Local]` (the `openapi/` corpus is gitignored — CI can never run the disk gate; local `dotnet test` still runs it). rivet-ts `scaffolder` merged → `v0.11.0` tagged; cold install + `task dev` smoke verified.
2. **Pin bumps — DONE.** rivet-ts default binary pin → 0.35.0; golden + showcase `Rivet.Attributes` → 0.35.0 (RV-026 clears everywhere); golden SDK pin 10.0.301 → 10.0.300 (brew ceiling on the new machine).
3. **rivet-ts polish batch — DONE** (tag `v0.11.1`): D1 module-local reshape (§9.10 + backend-pa-vsa synced, lifecycle gates assert the new shape); `result.data` forwarded when the synthesized schema is exact; peer warnings chased to source (rivet-ts vite peer widened to `^6 || ^7`; `openapi-typescript>typescript` allowedVersions rule in the emitted `pnpm-workspace.yaml`; the eslint warn is upstream `@nuxt/eslint-config`); routes-catch-domain-error recorded in rivet-ts AGENTS.md; meridian's TEMPORARY Rivet.Tool-checkout hack retired (dotnet flavor `task generate` = `dotnet tool restore` + published `dotnet-rivet`, e2e-verified); golden-meridian regenerated (all four flavors, plumb 0 everywhere). The one-time 2/200 vitest failure on this machine's first cold run is attributed to cold-pnpm-store timeouts in two lifecycle files (120s limit; 186s cold vs 52s warm total) — not reproduced since.

## Remaining / deferred

1. **P1 enforcement honesty (rivet, open tier) — THE NEXT TIER:** outbound body validation / extra-field leakage on .NET (Hono half deprioritized by decision); the `Define.File`/body-on-void escape hatches; runtime failure-envelope alignment between stacks. `docs/guides/runtime-validation.md` is the scope statement.
2. **IR→Zod for the dotnet/no-api scaffold flavors** — blocked on a FluentValidation→constraints channel; own project.
3. **Residual cosmetics (known, deliberately left):** cloudflare ~20% description loss on round-trip (mostly already-warned degradations); the misleading `unsupported body content-type` marker on schema-less bodies; snake_case *query-param* name fidelity (binding-semantics design question).
4. **P3 hygiene leftovers** from FABLE_GAPS §7.14–15: drift-detection story for .NET consumers; `php-reflector/` deletion + rivet-php composer pointer.

## How to verify everything

- rivet: `dotnet test` → 1242/1242 (includes `CliPipelineTests`, the disk gate).
- rivet-ts: `npx vitest run` → 200/200; `npx oxlint` + `npx tsc --noEmit` clean.
- plumb: `~/.meridian/plumb/plumb --self-test` → 64/64; harness `node --test "test/*.test.mjs"` in `~/.meridian`.
- A fresh `meridian init` scaffold passes `task plumb` with zero findings; `~/Sites/golden-meridian/` is a regenerated showcase of all four flavors (its README maps them).

## Where the detail lives

`FABLE_GAPS.md` (the audited register, status header current) · `docs/reference/import-profile.md` + `docs/reference/diagnostics.md` (what imports, what warns) · `docs/guides/runtime-validation.md` (enforcement scope) · `~/.meridian/plumb/FABLE_CONTRACT.md` (doctrine golden spec — amend FIRST) · rivet-ts `AGENTS.md` (working agreements incl. the plumb-zero gate) · `git log` on both repos — the commit messages carry the why.
