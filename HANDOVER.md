# HANDOVER — Rivet / rivet-ts / Meridian tooling

**Date:** 2026-07-13. **Scope:** `~/Sites/medway/rivet` (branch `main`; `v2` is kept as a pushed mirror of main), `~/Sites/medway/rivet-ts` (branch `main`; `scaffolder`/`v2` mirrors), `~/.meridian` (plumb on branch `v8`, `bin/meridian`), `~/Sites/golden` (.NET exemplar, private remote `golden`, branch `rivet-v2`), `~/Sites/golden-meridian` (one generated showcase per flavor). Releases live: NuGet `Rivet.Attributes`/`dotnet-rivet` **0.37.0** (0.35.0 → P1 enforcement 0.36.0 → net8 multi-target 0.36.1 → AcceptsBinary 0.37.0, all same-day); rivet-ts tag **v0.11.1**.

Most detail is deliberately NOT here — it lives where it's verifiable. This doc says what's true, what's decided, and what's next; for the how, read the commit messages (they're written to be read) and the docs listed at the bottom.

## Current corpus evidence — 16/25 re-admitted

`corpus/verified-profile.json` contains 16 corpora: 2,516 operations and 3,104
normalized component identities (2,764 schemas, 72 request bodies, 154
parameters, 95 responses, and 19 security schemes). The hardened production-CLI
gate is green 16/16; the full suite is green 1,764/1,764 plus 73/73 runtime tests
on net8/net9/net10; `task check` builds every sample and the docs; and the retained
physical audit independently recompiles both retained generated-source passes,
recomputes source-to-first semantics, and passes 16/16.

**The 16/25 local support count is re-admitted under both document and generated-
carrier fidelity.** The inventory records 7,851 carrier-sensitive owner/shape
classifications across 7,818 unique pointers: 6,813 named-property implicit-open
records, 15 explicit-open records, 2 closed records, 803 propertyless
object/dictionary shapes, 182 empty-schema `JsonElement` carriers, 31 nullable
composition branches, and 5 nested discriminators. Behavior mutations prove each
observed carrier shape class, including open extension data, closed and opaque
counterexamples, schema-valued dictionaries, Box null-without-widening, and
Spotify direct/nested discriminator values through real CLI output, compilation,
runtime deserialize/serialize, and re-emission.

The fixes remove reserved-header request-body rewriting, add extension-data
carriers without changing closed records or dictionaries, recognize Box's
null-only impossible-pattern branch, and give generated string enums exact runtime
wire conversion. The gate now fails closed on comparator process/report faults,
tracks public schema reference identity, requires explicit disposition-policy
approval, records extension owner pointers, and recomputes source-to-first audit
results instead of trusting retained summaries. Corpus artifacts and reports are
still local/gitignored, so 16/25 remains local proof rather than reproducible CI
proof.

The current profile's exact source classifications are one invalid empty Notion
parameter name plus ten ignored reserved Header Parameters (CircleCI, Docker,
and SendGrid), pinned by source hash, pointer, diagnostic, and cardinality. The
ignored Docker header is not authority to change its valid Request Body Object.
The older DocuSign-only source-defect and Twilio 21-delta descriptions were SIX
era evidence and are not the current retained-profile result.

`SIX.md` remains the historical specification for the first six-corpus replay
gate. Its old 6/6 and 16/16 results are superseded by the hardened 16/16 document
and carrier result above.

## What we did, and why

**The v2 pivot (rivet):** the v1 C#→TS client emitters are gone. Rivet's job is now *contracts → openapi.json*; TS clients come from the OpenAPI ecosystem (openapi-typescript + openapi-fetch). Everything below serves one intended promise: **nothing is dropped silently, and the spec and the runtime tell the same story.** The current replay gate is necessary but not sufficient evidence for that promise; carrier fidelity must be proved separately.

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
- **The corpus-admission freeze is lifted.** The parked sequence may resume only
  through its approval-gated order and the same document/carrier gates.
- **"Clean" has two independent dimensions:** document fidelity and carrier
  fidelity. Raw OpenAPI provenance may prove the former; it cannot prove the
  latter when generated runtime types cannot receive and re-emit a valid value.
- **16/25 is the current local support count.** Do not add a corpus to that count
  until it independently passes the hardened document and carrier gates.

## Claimed unsupported / degraded-by-design

Current behavior list: `docs/reference/import-profile.md`. Outside the SIX
surface, TRACE is dropped with `RIV3003`; callbacks, webhooks, and links have no
generated C# representation; unresolved/cyclic aliases and unsupported general
schema algebra degrade with diagnostics; reserved OpenAPI headers are dropped
loudly; and unusable discriminator mappings lose dispatch semantics with
`RIV3005`. Path/query/header/cookie parameters and request bodies now retain
their explicit locations independently, and response headers retain typed schema
  metadata. Diagnosed or marked degradation is not full support. The reference now
  reports the re-admitted local corpus result. Current known provider-private
  losses include Kubernetes apply/merge/union metadata; those remain explicit
  non-claims, not standard OpenAPI support failures. Twilio Path Item descriptions
  are also lost, but they are standard annotations rather than HTTP wire behavior.

## Done since this doc was written (2026-06-12, same day)

1. **The release act — DONE.** rivet 0.35.0 shipped off `v2` (tag `v0.35.0`: GitHub release binaries ×4 + NuGet `Rivet.Attributes`/`dotnet-rivet`). Two CI gaps fixed en route: `publish.yml` now `npm ci`s `Rivet.Tests/js`, and `CliPipelineTests` is `[Category=Local]` (the `openapi/` corpus is gitignored — CI can never run the disk gate; local `dotnet test` still runs it). rivet-ts `scaffolder` merged → `v0.11.0` tagged; cold install + `task dev` smoke verified.
2. **Pin bumps — DONE.** rivet-ts default binary pin → 0.35.0; golden + showcase `Rivet.Attributes` → 0.35.0 (RV-026 clears everywhere); golden SDK pin 10.0.301 → 10.0.300 (brew ceiling on the new machine).
3. **rivet-ts polish batch — DONE** (tag `v0.11.1`): D1 module-local reshape (§9.10 + backend-pa-vsa synced, lifecycle gates assert the new shape); `result.data` forwarded when the synthesized schema is exact; peer warnings chased to source (rivet-ts vite peer widened to `^6 || ^7`; `openapi-typescript>typescript` allowedVersions rule in the emitted `pnpm-workspace.yaml`; the eslint warn is upstream `@nuxt/eslint-config`); routes-catch-domain-error recorded in rivet-ts AGENTS.md; meridian's TEMPORARY Rivet.Tool-checkout hack retired (dotnet flavor `task generate` = `dotnet tool restore` + published `dotnet-rivet`, e2e-verified); golden-meridian regenerated (all four flavors, plumb 0 everywhere). The one-time 2/200 vitest failure on this machine's first cold run is attributed to cold-pnpm-store timeouts in two lifecycle files (120s limit; 186s cold vs 52s warm total) — not reproduced since.
4. **P1 enforcement honesty (.NET) — DONE** (`0bdaaad`, unreleased on main; next NuGet release picks it up): the extra-field leak is closed — the typed-results validator checks the **value's runtime type** (derived AND upcast instances rejected; interface/abstract/`[JsonPolymorphic]` declared types accept subtypes; boxed nullables and `object` exempt); body-on-void via content-bearing results caught; declared-JSON-with-non-JSON-content-type caught; `Define.File` gained an opt-in `Invoke` (file content + declared content type on success, declared error statuses elsewhere; file results own their status, incl. 206 range). Violations throw `RivetContractViolationException` (InvalidOperationException subclass) and `RivetContractViolationHandler` maps them to `500 {code, message}` — same envelope as Hono. The Hono outbound half stays unbuilt by standing decision. `runtime-validation.md` rewritten as the scope statement. Bonus root-cause: `CliPipelineTests` intermittent full-suite failures were `WaitForExit(TimeSpan)` returning before the async stdout readers drained (`ef0f248`).
5. **P3 hygiene + housekeeping — DONE:** `--verify` drift gate (`470c431`: emit-in-memory, ordinal-compare against the committed spec, exit 1, never writes; CI recipe in `docs/reference/cli.md`); `php-reflector/` husk deleted + rivet-php composer pointers fixed (rivet-php `8f97735`); samples/ContractApi README de-rotted; both READMEs rewritten as the pitch, not the manual (rivet `9bbe8fc`: annotate-ASP.NET vs contract-first paths shown inline; rivet-ts `31e24f4`: write→generate→consume, −300 lines of duplicated-and-stale manual; GitHub repo descriptions updated too); stray FABLE_*/SCAFFOLDER working docs deleted from both repos; **golden pushed to a private remote** (`github-meridian:maxanstey-meridian/golden`, branch `rivet-v2`).

6. **Releases v0.36.0 + v0.36.1 cut and on NuGet (2026-06-12, same day):** 0.36.0 ships the P1 enforcement; 0.36.1 multi-targets Rivet.Attributes net8.0/9.0/10.0 (net9-only locked out .NET 8 LTS Functions hosts).
7. **Consumer migrations to the v2 client style — DONE (2026-06-12):** speechscribe-azure (branch merged into `uploads`, pushed; hand-owned openapi-fetch facade + types layer; local dotnet-rivet manifest — the GLOBAL tool stays 0.34.3 because reel needs v1 behavior); casebridge (local branch `rivet-v2-client` ae88759, NOT pushed — client Azure DevOps; Rivet.Attributes was floating "*" and is now pinned); lagon azure-functions (local commit 4b11434, NOT pushed; 0.22.2→0.36.1 source-compatible, zero code changes). NOT migrated by decision: confer (superseded), reel + iahg (old pins keep working). Found en route: speechscribe has UI flows hitting endpoints absent from the contract (raw apiFetch helper covers them; worth annotating someday); openapi-typescript types format:binary as string (one documented cast at casebridge's multipart upload).

8. **v0.37.0 (2026-06-12): `.AcceptsBinary(contentType)`** — raw binary request bodies as a contract concept (spec-only; TInput lowers to route/query; imports back from `format: binary`; the old importer behavior of reshaping octet-stream bodies into IFormFile/multipart was a silent wire change and is gone). Motivating consumer: speechscribe's chunk-upload TODO — both its "Rivet can't do this" TODOs are now retired, its delete endpoint contract-ized, and the facade's raw `apiFetch` escape hatch deleted: the contract covers speechscribe's entire wire surface (28 ops).

9. **Legacy local round-trip audit — DONE for its bounded scope; not a full-gate result (2026-06-12, see `FABLE_ROUNDTRIP.md`):** GitHub corpus (1,099 ops) imported → re-emitted → scripted diff. The historical comparator reported zero operation-level deltas for 32% of operations and found 11 classes of drift. Those results and the later 86% projection are comparator-only local evidence; the terms `semantic clean` and `cleanOps` no longer apply. They do not cover the `SIX.md` semantic surface, unsupported-marker requirement, integrity validation, or fixed point.

10. **SIX second pass — historical old-gate closure (2026-07-12):** the real-disk replay gate was 6/6 for 848 operations and 1,597 normalized component identities (1,532 schemas + 58 request bodies + 7 security schemes). It proved inventory closure, vendor dispositions, `collectionFormat`, shared/unused request-body identity, artifact integrity, compilation, and fixed point under that gate. It did not prove generated-carrier fidelity and is superseded as a support claim by the admission freeze above.

## Remaining / deferred

1. **ADMISSION FREEZE — falsify the current result before corpus 17.** This is
   the only active corpus work. Do not make production fixes while establishing
   red; do not start Vercel/Slack/Bitbucket while this item is open.

   **Phase A — RED ESTABLISHED (2026-07-13): prove Docker at all three
   boundaries.** The corrected expectations are in:

   - `OpenApiImporterTests.Reserved_Content_Type_Header_Normalizes_A_Single_Binary_Request_Content`
   - `CliPipelineTests.Cli_Import_Maps_A_Finite_Content_Type_Header_To_Request_Content`
   - `RoundTripDiffTests.Content_Type_Header_Normalizes_A_Single_Request_Content`

   The proved red expectations are: the reserved header is diagnosed and omitted;
   `requestBody.content.application/octet-stream` remains authoritative;
   `application/x-tar` is not emitted; and a comparator presented with the
   current source/output pair reports request-content-type drift. All three tests
   fail before production code changes. The focused run fails 3/3, and the full
   run contains exactly these three failures before Phase B additions.

   **Phase B — RED/POSITIVE CONTROL ESTABLISHED (2026-07-13): prove
   generated-carrier behavior, not source replay.** Public disk/compiled-runtime
   tests now avoid raw schema provenance and show:

   - Explicit open object: a request object with a known `id` property and
     `additionalProperties: true` loses an unknown nested member and value after
     generated runtime deserialize/serialize.
   - Implicit open object: omitting `additionalProperties` produces the same loss.
   - Box nullable union: the enum-or-null `anyOf` shape compiles and round-trips
     JSON `null`, but fails the requirement to reject an arbitrary non-enum string.
   - Nested discriminator: a fixture preserving Spotify's nested Track/Episode,
     `allOf`, and single-value `type` enum structure rejects the valid Episode wire
     value both directly and through the nested union. This proves carrier failure,
     while dispatch remains unisolated behind string-enum deserialization.

   **Phase C — GREEN COUNTEREXAMPLES ESTABLISHED (2026-07-13): keep honest opaque
   carriers green.** Real CLI import/emission plus compiled-runtime tests prove:

   - inline `Dictionary<string, JsonElement>` and bare-`{}` `JsonElement` properties
     preserve nested valid values and re-emit open schemas;
   - a SendGrid-style matching email entry survives its generated dictionary,
     while opaque provenance re-emits `additionalProperties: false` and
     `x-patternProperties`;
   - a valid string dictionary survives under `maxProperties`, while
     `minProperties`, `maxProperties`, and the value `maxLength` re-emit.

   These are carrier/document proofs only. They do not claim CLR interpretation
   of opaque values or runtime enforcement of `maxProperties`.

   **Phase D — COMPLETE (2026-07-13): inventory carrier-sensitive shapes across
   all 16 sources.** `tools/roundtrip-inventory.py` now records deterministic
   owner-pointer groups with corpus, shape, expected generated carrier class, and a real
   behavior-test identity; profile tests resolve every referenced test and
   mutation-cover every required family. Current observed counts are:

   - 6,813 named-property objects with omitted `additionalProperties` (records,
     present in all 16 corpora);
   - 15 named-property objects with `additionalProperties: true` (records, all
     Spotify request bodies);
   - 2 named-property objects with `additionalProperties: false` (Zoom records);
   - 676 propertyless objects with omitted `additionalProperties`, 123
     schema-valued dictionaries, 3 propertyless closed objects, and 1
     propertyless explicit-open object;
   - 182 empty schemas carried as `JsonElement`;
   - 31 nullable composition branches (16 records, 9 scalars, and 6 unions);
   - 5 nested discriminators (Spotify unions).

   Named-property schema-valued `additionalProperties`, Parameter Object `content`,
   Encoding Objects, external-value examples, component
   headers/examples, cross-path references, and explicit discriminators are zero
   in the current roster and remain explicit profile facts. The profile retains
   all 7,851 owner/shape classifications across 7,818 unique pointers; generic keyword totals are no longer
   used as object context evidence.

   **Phase E — RED CONTROLS ESTABLISHED (2026-07-13): harden proof paths.** The
   focused mutations now prove:

   - **red:** comparator exit 1 is accepted when empty summary/details JSON exists;
   - **red:** a summary missing required fields/counts deserializes to defaults and
     passes;
   - **green:** the actual C# unsupported-marker scanner detects a planted marker;
   - **red:** the retained physical audit passes when first and second OpenAPI are
     identically mutated away from source while old reports remain green;
   - **red:** public schema `$ref` replaced by an equivalent inline shape is
     accepted;
   - **red:** extension inventory facts lack owner pointers, and ordinary
     `--update-profile` approves a reviewed disposition change.

   All controls are green as of Phase F; the scanner control required no fix.

   **Phase F — green implementation order.** After the red result and affected
   corpus table are reviewed: (1) **COMPLETE (2026-07-13)** remove reserved-header
   request-body rewriting from importer and comparator. Docker now preserves
   `application/octet-stream`, diagnoses and omits the reserved header without an
   unsupported marker, and passes the real disk gate; focused adjacent header
   controls remain green. (2) **COMPLETE (2026-07-13)** open records now retain
   typed named properties plus collision-safe `[JsonExtensionData]` for explicit,
   implicit, empty, inline, generic, polymorphic-variant, and flattened composition
   shapes; closed records and opaque dictionaries remain unchanged. Schema-valued
   additional properties survive real CLI generation, runtime round-trip, Roslyn
   walk, and OpenAPI re-emission; all 16 real corpus gates remain green. (3)
   **COMPLETE (2026-07-13)** Box's canonical impossible-pattern nullable branch
   is treated as null-only, so null survives without widening the enum variant to
   arbitrary strings. (4) **COMPLETE (2026-07-13)** generated string enums now
   carry a runtime string converter and explicit wire names for every member;
   Spotify direct Episode values and nested Track/Episode dispatch deserialize and
   serialize their valid wire discriminator exactly, and the real corpus gate is
   green. (5) **COMPLETE (2026-07-13)** inventory and profile evidence are
   regenerated; all fail-closed, reference-identity, owner-pointer, policy-
   approval, and source-to-first mutation controls are green; the full suite is
   1,764/1,764; all runtime targets pass; `task check` passes; and the recomputed
   physical audit passes 16/16.

   **Re-admission rule:** the existing 16 corpora are re-admitted from zero under
   the new definition. A corpus is clean only when document fidelity and carrier
   fidelity both pass; every observed carrier-sensitive shape class has generated
   runtime deserialize/serialize proof; unsupported wire shapes diagnose or mark; raw
   provenance is not the sole mechanism restoring an otherwise unrepresentable
   value; and all comparator/gate mutation controls are green. Publish a support
   count only after that re-admission. **Result (2026-07-13): 16/25 re-admitted
   locally under the new definition.**

   **Parked 23-corpus sequence:** after re-admission, resume the approval-gated
   capability order Vercel/Slack/Bitbucket (full `additionalProperties` state),
   Stripe (request Encoding Objects), Jira (nested discriminator/provenance),
   GitHub (component headers/examples/unions), then DigitalOcean (cross-path
   reference/provenance). Cloudflare and Discord remain explicit non-claims.
   The projected endpoint remains 23 source corpora / 5,746 operations / 5,888
   named schemas, but no milestone count is valid until each corpus clears both
   gates.
2. **Inverse coverage check** — `--check` warning for HTTP-routed actions that never `Invoke` a contract. Nothing audits implementations-without-contracts; that blind spot is how speechscribe's chunk endpoint stayed invisible for months.
3. **IR→Zod for the dotnet/no-api scaffold flavors** — blocked on a FluentValidation→constraints channel; own project.
4. **Downstream pin refresh when convenient:** rivet-ts default binary pin (0.35.0) and golden/golden-meridian Rivet.Attributes (0.35.0) → 0.37.0. Cosmetic — 0.36.x/0.37.0 changed enforcement and added AcceptsBinary; emission for existing contracts is unchanged.
5. **Residual cosmetics (known, deliberately left):** wider-corpus findings such
   as cloudflare description drift remain outside the SIX result; §7.14 dev-loop
   nits (watchedFiles race, artifacts-on-error-exit) remain deferred.
6. **Awaiting Max's push (client-org remotes, not ours to push):** casebridge `rivet-v2-client` (`ae88759`, merge into `gdpr-ui` when his WIP lands) and lagon azure-functions (`4b11434`).

## Current baseline and investigation finish line

- Current inventory: `python3 tools/roundtrip-inventory.py` proves profile,
  keyword, component, extension-owner, and carrier-sensitive evidence closure.
- Legacy replay baseline: before falsification, `dotnet test ./Rivet.slnx` gave
  Rivet.Tests 1,735/1,735 and Rivet.RuntimeTests 73/73 on net8/net9/net10,
  including the green 16/16 old-gate run. That historical result is superseded by
  the current hardened 16/16 result.
- Investigation red finish line: Docker fails independently at importer, CLI,
  and comparator boundaries; explicit/implicit open-object runtime round trips
  lose a planted additional member; Box enum-or-null round-trips null but accepts
  an arbitrary string; the Spotify-shaped Episode mutation is rejected directly
  and nested; opaque/dictionary counterexamples remain green; and the
  carrier-sensitive source inventory names every affected pointer/corpus.
- Re-admission green finish line: **ACHIEVED 2026-07-13.** All proved failures are fixed; fail-closed gate
  mutations pass; the physical audit recomputes source-to-first comparison; all
  16 sources pass document and carrier gates; `task format:verbose` and
  `task check` pass; and the resulting support count is derived from that run.
- rivet-ts: `npx vitest run` → 200/200; `npx oxlint` + `npx tsc --noEmit` clean.
- plumb: `~/.meridian/plumb/plumb --self-test` → 64/64; harness `node --test "test/*.test.mjs"` in `~/.meridian`.
- A fresh `meridian init` scaffold passes `task plumb` with zero findings; `~/Sites/golden-meridian/` is a regenerated showcase of all four flavors (its README maps them).

## Where the detail lives

`SIX.md` (authoritative six-corpus second-pass specification) · `FABLE_GAPS.md`
(audited register) · `FABLE_ROUNDTRIP.md` (historical bounded audit evidence) ·
`docs/reference/import-profile.md` + `docs/reference/diagnostics.md` (current
import behavior and diagnostics) · `docs/guides/runtime-validation.md`
(enforcement scope) · `~/.meridian/plumb/FABLE_CONTRACT.md` (doctrine golden
spec — amend FIRST) · rivet-ts `AGENTS.md` (working agreements incl. the
plumb-zero gate) · `git log` on both repos — the commit messages carry the why.
