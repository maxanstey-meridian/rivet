# FABLE_PHASE0.md — conformance-gate gap analysis (Phase 0)

Written 2026-06-11. This is the output of FABLE_REWRITE.md "Phase 0 — the five-minute
experiment", produced by standing up the conformance gate itself
(`Rivet.Tests/OpenApiConformanceTests.cs`, per FABLE_TEST_FIXES.md SYS-2). It is the
input worklist for Phase 1 ("bring `OpenApiEmitter` to conformance-gate green").

## The gate

| # | Check | Implementation | Oracle |
|---|---|---|---|
| 1 | Lint | `spectral lint` (ruleset `Rivet.Tests/js/.spectral.yaml`, `spectral:oas`) over every emitted spec | zero errors; warnings reported in test output, not failing |
| 2 | Consume | `openapi-typescript` over every emitted spec, then `tsc --strict` over its output | both exit 0 |
| 3 | Self-loop | emit → import → compile → walk → emit, twice; spec after one import ≡ spec after two (`JsonNode.DeepEquals`) | emit∘import is a fixed point. First-loop *semantic* fidelity is pinned per-construct by `OpenApiRoundTripTests` (incl. the maximal contract's double round-trip); this check extends loop coverage across the corpus |
| 4 | Importer stability | pre-existing: `RealWorldImportTests` / `ImportMetricTests` (petstore, GitHub, Stripe, Twilio, kitchen-sink) | unchanged by this work |

Tooling is vendored in `Rivet.Tests/js` (`@stoplight/spectral-cli`, `openapi-typescript`,
`typescript`, alongside the existing `zod`) — `npm install` there once, then the gate runs
offline and deterministically via `node node_modules/.bin/...` (no `npx` downloads).
Set `RIVET_CONFORMANCE_DUMP=<dir>` to keep a copy of every emitted spec for triage.

## The corpus (10 fixtures)

| Fixture | Source | Surface |
|---|---|---|
| `maximal-contract` | `OpenApiRoundTripTests.MaximalContract_DoublRoundTrip_IsLossless` source | every primitive/nullable/collection/brand/enum/generic/nested shape, 5 verbs, multi-response, file upload, security overrides; emitted with `--security bearer` |
| `controller-annotations` | `TypeScriptCompilationTests.GeneratedOutput_PassesTscNoEmit` source | `[RivetClient]` controllers, ProducesResponseType, generics, IFormFile |
| `typed-results` | `TypeScriptCompilationTests.TypedResults_Endpoints_CompileTs` source | `Results<Ok<T>, NotFound>` family |
| `mixed-contracts-controllers` | `TypeScriptCompilationTests.ContractEndpoints_MixedWithControllers_Compile` source | merged contract + controller endpoints |
| `file-endpoints-query-auth` | merged `FileEndpoint_WithQueryAuth_FullPipeline` + `FileEndpoint_ByteArray_CompilesTs` sources | `Define.File`, `QueryAuth`, `byte[]`, `ProducesFile`, typed file input |
| `validation-metadata` | distilled from `MetadataAttributeTests` fixtures | DataAnnotations constraints, `RivetConstraints` (MultipleOf/ExclusiveMinimum), description/example/default, read/write-only, deprecated, `FormEncoded` |
| `contractapi-sample` | `samples/ContractApi` (Contracts + Models + Domain, real files) | the daily-use sample contract; `--security bearer` |
| `contract-sample-json` | `Fixtures/contract-sample.json` via `JsonContractReader` | rivet-ts-shaped contract JSON |
| `contract-tagged-union-json` | `Fixtures/contract-tagged-union.json` | tagged unions / discriminator (E11 surface) |
| `php-golden-contract-json` | `Fixtures/php-golden-contract.json` | the rivet-php reflector's golden output |

## Result matrix

P = pass, **F** = fail (skipped with `CONFORMANCE-GAP`), – = not applicable
(contract-JSON fixtures enter through `JsonContractReader`, not the importer, so the
self-loop's first emit is already the same code path).

| Fixture | 1 Lint | 2 Consume | 3 Self-loop |
|---|---|---|---|
| maximal-contract | P | P | P |
| controller-annotations | P | P | P |
| typed-results | P | P | P |
| mixed-contracts-controllers | P | P | P |
| file-endpoints-query-auth | P | P | ~~**F** (GAP-2)~~ P |
| validation-metadata | P | P | P |
| contractapi-sample | P | P | P |
| contract-sample-json | P | P | – |
| contract-tagged-union-json | P | P | – |
| php-golden-contract-json | ~~**F** (GAP-1)~~ P | ~~**F** (GAP-1)~~ P | – |

**Counts (original, 2026-06-11 a.m.):** lint 9/10, consume 9/10, self-loop 6/7
(+3 n/a). 24 passing rows, 3 skipped gap rows. Headline: the emitter is much closer
to conformance-green than feared — the maximal contract, the sample project,
controllers, typed results, validation metadata and tagged unions all pass every
applicable check today. Both hard failures cluster in two code paths.

**Counts (after Phase 1 WP-1.1 fixes, 2026-06-11):** lint 10/10, consume 10/10,
self-loop 7/7 (+3 n/a). 27/27 rows pass; zero `CONFORMANCE-GAP` skips remain.

## GAP-1 — dangling `$ref` for generic instantiation with missing template (php-golden, checks 1+2)

Exact errors:

```
spectral: invalid-ref at paths./products/paginated.get.responses.200.content.application/json.schema.$ref:
  '#/components/schemas/Collection_ProductDto' does not exist          (severity: error)

openapi-typescript 7.13.0 (exit 1):
  ✘ Can't resolve $ref at #/paths/~1products~1paginated/get/responses/200/content/application~1json/schema
```

Repro shape: `php-golden-contract.json` has an endpoint returning
`{kind: "generic", name: "Collection", typeArgs: [ref ProductDto]}` but **no
`Collection` template in its `types` array** (the PHP reflector emits the usage
without the definition). `OpenApiEmitter` names the instantiation
`Collection_ProductDto` and emits the `$ref`, but instantiation-to-component
synthesis walks `definitions` — no template, no component, no diagnostic.

- **Code path:** `OpenApiEmitter` generic mapping + `CollectGenericInstances`
  (OpenApiEmitter.cs ~:941) / component synthesis; upstream producer is the PHP
  reflector (contract JSON with dangling generic usage).
- **FABLE_REVIEW mapping:** E6 family (generic-instance collection walking
  definitions), enforceability-rule violation (silent drop, no named diagnostic);
  upstream PHP8 (hand-maintained golden fixture) is how the malformed input got
  blessed.
- **Fix sketch:** in the emitter, when a `Generic`'s template is absent from
  `definitions`: emit a loud named diagnostic and fall back to a free-form object
  schema (or fail emission) — never a dangling `$ref`. Optionally also fix the PHP
  golden to include the template.
- **Size:** small (≤ half-day incl. test): one guard in the generic-mono-instance
  path + a stderr/diagnostic assertion. Fixing the PHP reflector itself is separate
  (PHP-section, open question 1 in FABLE_REWRITE).
- **Resolution (2026-06-11):** fixed in `OpenApiEmitter.BuildSchemas` (the
  generic-instance loop): a missing template now emits a loud named stderr
  diagnostic and synthesizes a valid free-form object fallback component under the
  $ref'd name — never a dangling `$ref`. Test:
  `OpenApiEmitterTests.Generic_Instance_With_Missing_Template_Emits_Fallback_Component_And_Diagnostic`.
  Both `php-golden-contract-json` rows un-skipped and green. The PHP golden fixture
  was intentionally left malformed — it now exercises the guard.

## GAP-2 — emit∘import not a fixed point: synthesized input-record suffix grows every loop (file-endpoints-query-auth, check 3)

Observed: after one import the components are
`{StreamInput, ErrorDto, NotFoundDto, StreamInput2, PreviewInput, MediaInput, DownloadInput}`;
after two imports the same set **plus `StreamInput3`** — every loop mints a fresh
numbered record for the `stream` endpoint's synthesized param input.

Mechanism: the importer synthesizes `{endpointName}Input` for bare path/query params
(`stream` → `StreamInput` `{id}`). That collides with the real `StreamInput`
(`{id, quality}`); the I3 fix's shape-check correctly refuses name-only reuse and
mints `StreamInput2`. But on the next loop the shape-check again compares only
against the *unsuffixed* name — it never notices that `StreamInput2` (`{id}`) is an
identical-shape schema already present — so it mints `StreamInput3`, and so on
unboundedly.

- **Code path:** `ContractBuilder.ResolveParamInputType` (ContractBuilder.cs ~:209)
  + the shape-checked reuse/dedup added by FABLE_TEST_FIXES I.A-14.
- **FABLE_REVIEW mapping:** I3 (synthetic param-input collisions) — the residual
  half: dedup is collision-safe but not idempotent.
- **Fix sketch:** before minting a numbered name, shape-compare against existing
  numbered variants (`StreamInput2..N`) and reuse on exact shape match; or name
  synthesized inputs deterministically by qualified endpoint
  (`StreamingStreamInput`). Must keep the I.A-14 collision test green.
- **Size:** small-medium (~half-day): localized to importer naming/reuse, plus
  un-skip the self-loop row.
- **Resolution (2026-06-11):** fixed in `ContractBuilder.ResolveParamInputType` +
  new `SchemaMapper.FindNumberedSchemaWithShape`: before minting a fresh suffix,
  the importer now scans existing numbered component variants
  (`{base}2`, `{base}3`, …) and reuses the lowest-suffixed one whose shape matches
  exactly. Additionally, synthesized record names are segment-pascalized
  (`Naming.ToPascalCaseFromSegments`) so names containing underscores don't mutate
  on the next loop. Tests:
  `OpenApiImporterTests.Param_Input_Record_Reuses_Identically_Shaped_Numbered_Variant`
  (I.A-14 collision tests stay green); `file-endpoints-query-auth` self-loop row
  un-skipped and green.

## Non-gate findings (spectral warnings + spec inspection)

Reported by the gate but not failing it; each is a candidate Phase 1 line item.

- **W1 — `.Secure("scheme")` emits a security requirement with no matching
  securityScheme.** `oas3-operation-security-defined` on `maximal-contract`
  (`admin` on `/api/admin/cache.delete`) and `contractapi-sample` (3 ops). Only the
  CLI-level `--security` scheme lands in `components.securitySchemes`; per-endpoint
  `.Secure("admin")` overrides reference an undefined scheme — consumers reject
  these requirements. Code path: `OpenApiEmitter` security-scheme emission vs
  `SecurityMetadata.Scheme`. Fix: synthesize a scheme entry per distinct
  `.Secure(...)` name (size: small). This is one warning away from being a GAP —
  recommend promoting to error in Phase 1.
  - **Resolution (2026-06-11):** fixed in `OpenApiEmitter.EmitCore`: every scheme
    referenced by an endpoint-level `.Secure(name)` without a definition now gets a
    synthesized default bearer `securitySchemes` component plus a loud stderr
    diagnostic (no definition source exists for endpoint-level names). Tests:
    `OpenApiEmitterTests.Security_PerEndpoint_Secure_Emits_SecurityScheme_Component`,
    `…_Matching_Cli_Scheme_Is_Not_Duplicated`. Zero
    `oas3-operation-security-defined` findings remain across the corpus.
- **W2 — `ActionResult<T>` without `[ProducesResponseType]` on a `[RivetClient]`
  controller loses `T` entirely.** Surfaced via `oas3-unused-component
  TaskDetailDto` on `controller-annotations`: `GET /api/tasks/{id}` (returns
  `Task<ActionResult<TaskDetailDto>>`) emits **`204 No Content` with no schema**.
  The unwrap exists (EndpointWalker.cs:575) and works when a ProducesResponseType
  is present (ControllerEndpointTests:1011) — the no-attribute path drops the
  success type, then the void default kicks in. A-section silent-drop class (A8/A11
  adjacent). Fix in `EndpointWalker` response synthesis (size: small-medium).
  - **Resolution (2026-06-11):** fixed in `EndpointWalker.ExtractAllResponseTypes`:
    a bare `ActionResult<T>` (no attributes, not a typed result) now implies a
    200/T success response; explicit `[ProducesResponseType]` still wins. Tests
    (test-first):
    `ControllerEndpointTests.Controller_Bare_ActionResultOfT_Implies_200_Success_Response`,
    `…_With_Explicit_Produces_Keeps_Attribute_Status`. The
    `oas3-unused-component TaskDetailDto` warning on `controller-annotations` is gone.
- **W3 — GET-input record components left unused.** `oas3-unused-component` on
  `SearchInput` (maximal) and `StreamInput` (file-endpoints): GET inputs are
  flattened into query parameters (correct), but the input record is still emitted
  as a component. Cosmetic bloat; fix = don't emit components only referenced as
  flattened inputs, or reference them via `x-rivet-input` (size: small; interacts
  with the Phase 1 `x-rivet-*` design).
  - **Resolution (2026-06-11): accepted as-is.** Still exactly the same two
    warnings (`SearchInput` on maximal, `StreamInput` on file-endpoints). The input
    record component is what makes the synthesized-input reuse (GAP-2 fix) and the
    typed client surface work through the self-loop — suppressing it would trade a
    cosmetic warning for real round-trip loss. Revisit with the 3.1 migration
    (WP-1.4) if at all.
- **W4 — style-tier warnings** (every fixture): `info-contact`, `info-description`,
  `oas3-api-servers`, `operation-description` (where authors omitted
  `.Description`), `operation-tag-defined` (tags used but no global `tags` array —
  trivially fixable in the emitter by emitting the global tag list; size: tiny and
  worth doing for docs-UI consumers). Counts across the corpus:
  `operation-tag-defined` 54, `operation-description` 42, `oas3-api-servers` 11,
  `info-description` 11, `info-contact` 11.
  - **Resolution (2026-06-11):** `operation-tag-defined` fixed —
    `OpenApiEmitter.EmitCore` now emits the global `tags` array from the distinct
    operation tags (test: `OpenApiEmitterTests.Global_Tags_Array_Declares_All_Operation_Tags`);
    0 findings remain. The rest are accepted style-tier findings: `operation-description`
    (36) is author-omitted `.Description`, and `info-contact`/`info-description`/
    `oas3-api-servers` (10 each) reflect the fixed info block / no servers list —
    candidates for CLI flags later, not emitter defects.

## Phase 1 re-run (2026-06-11, after the fixes above)

Full corpus spectral findings: **0 errors**; warnings reduced to
`operation-description` 36, `info-contact` 10, `info-description` 10,
`oas3-api-servers` 10, `oas3-unused-component` 2 (the accepted W3 pair).
`operation-tag-defined` and `oas3-operation-security-defined` are gone.
Alongside the gap fixes, WP-1.1's `x-rivet-input-type` emission and
`x-rivet-contract`/`x-rivet-endpoint` operation extensions landed (importer prefers
the extensions, convention stays as fallback; round-trip pinned by
`OpenApiRoundTripTests.UnconventionalCasing_RoundTrips_Losslessly_Via_XRivet_Extensions`
and `…Inline_Multipart_Body_Pins_Input_Record_Name_Via_XRivet_InputType`).

## Where the Phase 1 worklist points first

1. **GAP-1** — dangling generic `$ref` (emitter guard + diagnostic). Cheapest, and
   it's the only thing keeping two checks red.
2. **GAP-2** — idempotent input-record synthesis in the importer (finishes I3).
3. **W1** — `.Secure()` scheme synthesis, then promote
   `oas3-operation-security-defined` to a gate error.
4. **W2** — `ActionResult<T>` bare-controller success response (A-section fix, also
   unblocks deleting the legacy client emitters with confidence).
5. **W4 tags** — emit the global `tags` array (one-liner-ish, removes 54 warnings).
6. Then the planned Phase 1 items ride on the now-standing gate: A1/A3/R1 fixes and
   the 3.0→3.1 migration (SYS-1), each verifiable by un-skipping/extending rows.

## Suite status

Original (2026-06-11 a.m.): `OpenApiConformanceTests`: 27 rows — 24 pass, 3 skipped
with `Skip="CONFORMANCE-GAP: …"` (individually un-skippable repros for GAP-1 ×2 and
GAP-2 ×1). Full suite green.

**After Phase 1 WP-1.1 fixes (2026-06-11):** all 27 conformance rows pass, zero
skips. Full suite: 1215 tests, 100% green (9 new tests covering GAP-1, GAP-2, W1,
W2, W4 and the WP-1.1 extensions).

## Phase 2 notes (2026-06-11) — wrapper-decision evidence (WP-2.1/WP-2.2)

Recorded per FABLE_REWRITE_PLAN.md Phase 2 ("Named-method-wrapper decision
criteria"). Evidence only — the decision waits on WP-2.3 (real-app migration,
user task). Source: `SampleProjectOpenApiFetchTests.cs` (the openapi-fetch
consumer + the dual-run against the booted ContractApi sample). `openapi-fetch`
0.17.0 added to `Rivet.Tests/js` devDependencies.

**Dual-run result:** zero behavioral divergences. Seven scenarios (GET 200 JSON,
POST 201, DELETE-with-route-param 204, DELETE 404 error path, PUT 204, GET void
health, queryAuth avatar URL) produce identical statuses and bodies through the
generated rivet.ts client and the openapi-fetch consumer. Only representational
difference: "empty body" surfaces as `undefined` from the rivet client vs
`{}`/`""` from openapi-fetch (parse-failure fallback on empty error bodies) —
normalized in the comparison, but real apps that branch on falsiness will notice.

### (a) Call-site ergonomics

Same endpoint (invite, POST 201 + 422), both styles:

```ts
// rivet.ts generated client
const invited = await members.invite({ body });        // Promise<InviteMemberResponse>, throws RivetError
// openapi-fetch
const { data, error, response } = await api.POST("/api/members", { body });
if (error) { /* handle */ }                            // data: InviteMemberResponse | undefined
```

Diff noise per call: the path string + HTTP verb move into every call site
(`api.POST("/api/members/{id}/role", { params: { path: { id } }, body })` vs
`members.updateRole({ params: { id }, body })`), route params gain one nesting
level (`params.path.id` vs `params.id`), and the happy path costs an explicit
`if (error)`/`data!` dance where the rivet client throws. Endpoint renames become
find-and-replace on path literals instead of symbol renames; conversely the
openapi-fetch style has no generated function surface to maintain at all.

### (b) Per-status result discrimination

The rivet client's `{ unwrap: false }` returns a status-discriminated union —
`InviteResult = { status: 201; data: InviteMemberResponse } | { status: 422;
data: ValidationErrorDto } | …` — so `result.status === 422` narrows `data`.
openapi-fetch collapses to `data` (union of 2xx bodies) vs `error` (union of
non-2xx bodies), with `response.status` as a separate, unlinked `number`.

**Concretely from the sample: nothing is lost.** Every ContractApi operation has
at most one typed success body and one typed error body, so `error !== undefined`
narrows exactly as well as `status === 422`. The loss only materializes when one
operation declares ≥2 distinct error (or success) body types — then openapi-fetch
gives an undiscriminated union that `response.status` cannot narrow at the type
level. None of the sample's endpoints hit this; check the real app in WP-2.3.

### (c) Rivet-specific semantics

- **queryAuth** (avatar): the spec marks `token` a required query param, so
  openapi-fetch demands `params: { query: { token } }` on *every* call; injection
  once via config needs a hand-written `onRequest` middleware (supported, but
  it's bring-your-own and type-blind). The rivet client emits `avatarPath`/
  `avatarUrl` builders — typed token, usable directly in `<img src>`/media
  players without fetching. openapi-fetch has **no URL-builder equivalent at
  all**; producing a media URL means hand-assembling the string, untyped.
- **File endpoints**: rivet client returns `Blob` (`blob: true` baked in);
  openapi-fetch needs `parseAs: "blob"` per call, and openapi-typescript types
  the `image/jpeg` binary response as `string`, not `Blob`.
- **Brands**: erased, as predicted — `Email` (`x-rivet-brand`) becomes plain
  `string` in `schema.d.ts`; the rivet client emits `string & { __brand: "Email" }`.
  (Wire-format note, orthogonal to the client choice: the sample's `Email` lacks
  a JSON converter, so the *server* round-trips it as `{ value: … }` while both
  client type surfaces say `string` — both clients mistype it identically.)

Score so far: (a) hurts mildly (mechanical, greppable), (b) does not hurt on the
sample, (c) hurts for queryAuth/file/brand consumers. Per the plan's "≥2 of
(a)–(c)" bar, the wrapper question hinges on whether WP-2.3's real app uses
queryAuth/file endpoints and multi-error-status operations in anger.

## Golden migration (WP-2.3)

2026-06-11. `~/Sites/golden` migrated from the generated-client pipeline to
v2 (`openapi.json` → `openapi-typescript` → `openapi-fetch`) on branch
`rivet-v2`, uncommitted, for review. `apps/api-ts` does not consume
`@golden/contracts`; the UI is the only consumer.

**Shape:** `task generate` now runs the v2 tool (local-checkout `dotnet run`,
marked TEMPORARY in the Taskfile until v2 publishes) then
`openapi-typescript → generated/schema.d.ts`. Old `generated/{client,types,rivet.ts}`
(6 files, ~390 lines) deleted; replaced by `generated/{openapi.json,schema.d.ts}`
plus a 34-line hand-written facade at `packages/contracts/src/index.ts`
(mirrors rivet-ts's client-package-emitter: `paths`/`components` re-export,
`components["schemas"]` DTO aliases for `NoteDto` et al., `createClient` +
`configureRivet`/`client` pair). Package exports collapsed from
`./client|./rivet|./types` to a single `"."`. `openapi-fetch` dep +
`openapi-typescript`/`typescript` devDeps live on `@golden/contracts`.

**Call sites:** 2 awaited client calls (both in
`apps/ui/app/pages/index/composables/useRivetNotes.ts`), plus 2 type-only
import sites and 1 plugin bootstrap. UI-layer diff: **3 files, +15/−25
(net −10 lines)**.

Representative pairs:

```ts
// load() — before
try {
    const result = await notesClient.list({ unwrap: false });
    if (result.isOk()) { notes.value = [...result.data.notes]; return; }
    errorMessage.value = "Unable to load notes.";
} catch { errorMessage.value = "Unable to load notes."; }
finally { isLoading.value = false; }
// load() — after
const result = await client.GET("/api/notes").catch(() => null);
if (result?.data) { notes.value = [...result.data.notes]; }
else { errorMessage.value = "Unable to load notes."; }
isLoading.value = false;
```

```ts
// create() — before
const result = await notesClient.create({ body: { title, body } }, { unwrap: false });
if (result.status === 201) { await load(); return true; }
errorMessage.value = errorMessageFrom(result.data) ?? "Unable to create the note.";
// create() — after
const result = await client.POST("/api/notes", { body: { title, body } }).catch(() => null);
if (result?.data) { await load(); return true; }
errorMessage.value = errorMessageFrom(result?.error) ?? "Unable to create the note.";
```

```ts
// bootstrap — before / after (unchanged but for the import)
import { configureRivet } from "@golden/contracts/rivet";   // before
import { configureRivet } from "@golden/contracts";          // after (facade preserves the configure-once seam)
```

**Narrowing analysis.** The watched case — 409 vs 422 on create — did not
break, and typing *improved*: the old `CreateResult` union carried an
`Exclude<number, 201|409|422> → data: unknown` catch-all arm, so the error
path was `ErrorResponse | unknown` ⇒ `unknown` (hence golden's
`errorMessageFrom(data: unknown)` duck-typing). openapi-fetch types `error`
as `ErrorResponse` exactly (409 and 422 share the envelope). Lost: the
status-literal discrimination itself (`result.status === 201` narrowing
`data`; `response.status` is now an unlinked `number`) and the `.isOk()`
helper family — golden branched on neither beyond success/failure. The loss
only bites when one operation declares ≥2 *distinct* error body types, which
Meridian's single-`ErrorResponse`-envelope doctrine rules out by
construction. Behavioral note from WP-2.2 (empty-body `undefined` vs `{}`)
is moot here — no 204s.

**Gates.**
- `task test`: all three suites green — dotnet 3/3, api-ts 5/5 (incl. `tsc
  --noEmit`), ui 3/3.
- `task plumb`: `0 error, 6 warn, 0 info` — byte-identical to the
  pre-migration baseline. All 6 are pre-existing TO-pack tooling warns
  (MER-TO-002 ×2, -005, -012 ×2, -014: missing oxlint/oxfmt/eslint-vue/
  editorconfig/analyzers/CSharpier), unrelated to contracts; the README's
  "0/0/0" claim did not hold *before* the migration either.
  **Migration-introduced findings: zero.**
- Extra evidence (UI has no tsc gate): facade + call shapes pass
  `tsc --strict` standalone; runtime smoke through the committed facade
  against the booted API: GET 200, POST 201, dup POST 409, invalid POST 422.
  `task generate` re-run is byte-idempotent.

**Doctrine-rule friction (recorded, plumb untouched):**
- `MER-FE-006` (rivet.md#frontend-result-handling) genuinely pins the old
  convention: it flags any *awaited* `@*/contracts` client call inside a
  `try` block lacking literal `unwrap: false`. With openapi-fetch the
  textual remedy is unsatisfiable, so try/catch around client calls is
  effectively banned. Golden adapted (reasonably: openapi-fetch's design is
  "no throw on HTTP error") via `.catch(() => null)` for transport failure —
  rule does not fire — but the rule's message/remedy needs rewording for v2.
- `MER-FE-003` (components-no-client) greps the import specifiers
  `generated/rivet/client|contracts/client`; the v2 bare `@golden/contracts`
  specifier no longer matches, so the rule is a silent false-negative for
  v2-shaped repos (did not fire here either way — no component imports the
  client). Needs a pattern update.
- `MER-RV-020/021/024/025` survive intact: facade lives in `src/` (not
  `generated/`), `schema.d.ts` is `.d.ts`-exempt and header-carrying,
  `configureRivet` stays once-in-a-plugin (the facade deliberately keeps
  that seam), and RV-024's `rivet ... --output` Taskfile sniffing still
  matches the v2 invocation.

**Wrapper verdict (per the Phase 2 criteria):**
- (a) *ergonomics*: no hurt — net −10 lines; the path-literal-in-call-site
  cost is real but at golden's scale (1 path, 2 verbs) negligible, and
  dropping the throw/Result dual mode *simplified* the composable (no
  try/catch/finally scaffolding, one exit shape).
- (b) *per-status discrimination*: no hurt — strictly better typing on the
  multi-error-status operation, because the doctrine's uniform error
  envelope makes the per-status split typewise redundant. (b) can only hurt
  apps that violate the envelope rule.
- (c) *rivet-specific semantics*: **no evidence either way** — golden has no
  queryAuth, file endpoints, or brands. The WP-2.2 findings (no URL
  builders, per-call `parseAs: "blob"`, brand erasure) stand unrebutted but
  unweighed by a real app.

Score on the real app: 0 of (a)/(b) hurt, (c) untested. Under the plan's
"≥2 of (a)–(c)" bar the named-method wrapper is **not justified** by
WP-2.3's evidence; the only remaining case for it is queryAuth/file/brand
consumers, which would need a different exemplar to demonstrate.
