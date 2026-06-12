# FABLE_ROUNDTRIP — silent-drift audit of the import→emit round-trip (2026-06-12)

> **STATUS (2026-06-12, fix wave complete): 32% → 86% round-trip clean
> (948/1099 ops).** Every finding below is fixed or loud. #1/#2/#4: param
> wire-name pinning (route-token normalized matching + query-param
> `[JsonPropertyName]` pins + RIV1019). #3: enum pins trigger on
> emitted-value inequality. #6: nullable components propagate to $ref
> use-sites; required+nullable scaffolds as `required T?` (also killed
> #11b's 188 inline cases). #7: optional bodies import as nullable TInput
> (merge case marked). #8: redirect-only ops declare their 3xx. #9: the
> sentinel converts back to null. #10: `.AcceptsContentType()` /
> `.ProducesContentType()`. #5: loud `body-location` marker (model still
> lowers DELETE inputs to query). #11a (optional widens to nullable,
> 2261 props) is the one remaining conflation — documented in
> import-profile.md, not fixed. The audit script is committed as
> `tools/roundtrip-diff.py` and ratchets every `dotnet test` run via
> `RoundTripCorpusGateTests` against `Fixtures/roundtrip-baseline.json`;
> the residual flagged ops are the already-warned/marked content-type,
> merged-param and status-projection classes. Per-finding detail below is
> the ORIGINAL audit state, kept as evidence.

**Method:** `openapi/github.json` (732 paths / 1,099 ops) → `--from-openapi` → 3,324 C# files → re-emit → scripted semantic diff of original vs re-emitted spec. Every headline finding re-validated against freshly built HEAD (post-0.37.0) with a minimal probe spec confirming zero stderr warnings — all findings below are **silent at HEAD**. Findings covered by import-profile.md, diagnostics.md, HANDOVER, FABLE_GAPS §2–§3, RIV warnings, or `[rivet:unsupported]` markers were excluded; the loud channels cross-checked honest (every warned class matched a real degradation 1:1; no warned class also occurred silently).

## New silent findings, worst first

1. **Snake_case path params duplicate into an invented required query twin** (import+emit, over-claim, wire change). Importer camelCases the param into the input record with no wire-name pin; emitter fails to match `camelCase(member)` against the route token → emits the property as `required:true` query AND the route token as a separate untyped path param. `GET /advisories/{ghsa_id}`: re-emit has `ghsa_id` (path) + `ghsaId` (query, required). **356 ops / 429 duplicated params.** Fix: param wire-name pinning (or loud emit warning when an input property matches no route token).
2. **Hyphenated route tokens: template rewritten with a colliding token** (emit, wire change). `/enterprises/{enterprise}/teams/{enterprise-team}/…` re-emits as `/enterprises/{enterprise}/teams/{enterprise}/…` — two params collapsed; structurally invalid OpenAPI. **12 ops.**
3. **Enum wire values case-mangled** (emit, breaks both directions). Emitter camelCases member names; values differing from camelCase only by case are rewritten silently (`COLLABORATOR`→`collaborator`, `Ready`→`ready`). Sanitization-changed members get `[JsonStringEnumMemberName]` pins; case-only changes do not. **63 properties / 22 enum schemas.** Fix: pin whenever emitted value ≠ original, case-sensitive — the enum half of the 0cc54e3 wire-name fix.
4. **Bodyless POST/PUT/PATCH gain an invented `required:true` JSON body** (emit, over-claim). Unmatched path-param property lands in a fabricated body. `PUT /app/installations/{installation_id}/suspended`. **66 ops.** Same root cause as #1.
5. **DELETE-with-body: body dissolved into required query params** (import, wire change). DELETE treated as bodyless; body props relocate to query unmarked — `DELETE /applications/{client_id}/grant` moves an OAuth `access_token` into the URL. **16 ops.**
6. **Required+nullable `$ref` properties lose nullability** (import, over-claim). Component-level `nullable:true` dropped on import of named schemas (GitHub's `nullable-*` components). **139 properties / 17 components** — clients type them non-nullable, break at runtime.
7. **Request-body `required` drifts stronger** (emit, over-claim). `required:false`→`true` on **50 ops** (body optionality isn't modeled; only nullable body schemas survive optional).
8. **Redirect-only endpoints gain a fabricated `200 Success`** (import). `GET …/logs` declares only 302; re-emit adds 200. **8 ops**, no RIV2005.
9. **JSON `null` example values leak the internal sentinel string** (import bug). `value: null` → `"openapi-json-null-sentinel-value-2BF93600-…"` in the re-emitted spec. **45 occurrences.**
10. **`application/json` invented beside `text/*` content** (emit, over-claim). `POST /markdown/raw` gains a JSON request variant; `/markdown`, `/zen` responses gain JSON beside text. **4 instances.** Direct sibling of the fixed octet-stream→multipart class.
11. **Optional/nullable/required conflation** (import, under-claims, largest by volume). (a) optional non-nullable props re-emit nullable — **2,261 properties**; (b) required+nullable inline props keep null but drop `required` — **188 schemas**. import-profile currently claims nullable imports cleanly "in both forms" — false.

Minor: brand-alias components lose `format`/`readOnly`/description (21 `format: uri` drops); enum-typed property defaults dropped (3) despite import-profile claiming defaults survive.

## Totals

1,099 ops compared; 1,087 path-identical (12 corrupted by #2). **357 ops (32%) round-trip with zero operation-level deltas.** Bulk of the 742 flagged traces to two root causes.

## Verdict

Two structural blind spots, both the octet-stream class:
1. **Parameter identity is the un-pinned half of the wire-name fix** — properties got `[JsonPropertyName]` pinning in `0cc54e3`, enum members only partially, params not at all. Anywhere camelCase diverges from the original (snake_case path params, hyphenated tokens, case-variant enum values), the emitter silently invents/duplicates/relocates/rewrites wire artifacts. Findings #1–#4 share one fix-site family.
2. **The contract model has fewer optionality axes than OpenAPI** (body-required, required-vs-nullable, redirect-only responses) and every missing axis resolves silently toward a STRONGER claim.

Fixing pin-coverage for params + enum members, the nullable-component drop (#6), and the small inventions (#8/#9/#10) would eliminate every over-claim found; the remainder is honest-but-undocumented weakening that belongs in import-profile.md.
