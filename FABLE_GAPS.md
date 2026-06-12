# FABLE_GAPS — Rivet Capability Review (cross-repo)

**Date:** 2026-06-11. **Scope:** `~/Sites/medway/rivet` (C# DSL, Roslyn extraction, OpenAPI emission, runtime enforcement, OpenAPI importer) and `~/Sites/medway/rivet-ts` (TS contracts, Hono adapter, vite plugin, scaffolder), both at local HEAD.

**Method:** fresh-eyes capability audit, not a style review. Every row below was verified by reading the actual code path **and**, wherever a claim was suspicious, by writing a scratch contract and running the real tool: 10 C# probe contracts through `Rivet.Tool` (`/tmp/rivet-audit-cs/`), 8 TS probe contracts through the lowerer + `--from` emission (`/tmp/rivet-audit-ts/`), 5 real-world specs through the importer (`/tmp/rivet-audit-import/`), emitted specs through Redocly/Spectral/Prism/openapi-zod-client/swagger-typescript-api/openapi-typescript (`/tmp/rivet-audit-emit/`), live scratch servers curled for every runtime-violation class on both .NET (`/tmp/rivet-audit-dotnet-rt/`) and Hono (`/tmp/rivet-audit-hono/`), the same Widgets API authored both sides and semantically diffed (`/tmp/rivet-audit-xrepo/`), and a full scaffold → vite dev → contract-edit → break → recover loop executed end to end. Docs/README claims were treated as hypotheses; verdicts cite code or observed output.

**Gap classes:** **(a) bug** · **(b) unsupported-but-diagnosed** · **(c) unsupported-and-SILENT** (worst) · **(d) deliberate out-of-scope, should be documented**.

> **Status ledger (2026-06-12)** — this register is the audit snapshot; the rows below describe 2026-06-11 HEAD and are NOT individually updated. Since then, on `v2`:
> **P0 fixed** `d69cb08` (+ rivet-ts `e1bd060`) · **P2 fully shipped** `d693a5e..9701370` · **inbound constraints shipped** `cd5caba` · **importer [property:] crash fixed** `0cc45e3` · **disk-pipeline B-series fixed with a new e2e CLI gate** `06eb9b4..3f183b0` (loose-file refs, reserved names + wire-name pinning, case-insensitive name registries + directory args, embedded example-ref inlining, `[RivetUnion]` oneOf round-trip) · docs passes `51259c8`/`c4b51f9`/`083db9c`.
> **P1 enforcement honesty shipped (2026-06-12, .NET side):** the §4.1 extra-field leak (derived AND upcast instances rejected via value-runtime-type check; `[JsonPolymorphic]`/interface/abstract exempt), body-on-void (content-bearing results caught), content-type conformance, `Define.File` opt-in `Invoke` (file content + declared content type + declared error statuses), and the failure envelope (`RivetContractViolationException` + `RivetContractViolationHandler` → `500 {code,message}`, aligned with Hono's shape). The Hono outbound half (§4.2) stays unbuilt **by decision** ("as soon as it gets to just-MVP it's a TS backend"). Constraint *values* are still host-enforced, not Rivet-enforced — that's the documented design, not a gap.
> **P3 hygiene shipped (2026-06-12):** `--verify` drift gate for committed specs (§7.14); `php-reflector/` deleted + rivet-php composer pointer fixed + `samples/ContractApi/README` CLI docs corrected (§7.15).
> **Still open:** the residual §7.14 dev-loop nits (watchedFiles race, artifacts-on-error-exit; rivet-ts now HAS test CI) and the cosmetics listed in `HANDOVER.md`, which is the current-state document.

---

## 0. Architecture facts that frame everything

1. **rivet-ts has no OpenAPI emitter.** It lowers TS → contract JSON; the .NET binary (`Rivet.Tool --from`) is the sole emitter (`rivet-ts/src/vite.ts:138-145`). OpenAPI parity between the two authoring sides is therefore by construction — every divergence below comes from what each *producer* puts into the contract JSON.
2. The C# CLI pipeline emits **only `openapi.json`** at HEAD (`EmitPipeline.cs:34-75`). `ContractEmitter.cs` exists only as the deserialization schema for `--from`.
3. The shared model (`TsType`) can represent things the C# DSL cannot author (`TaggedUnion`, `IntUnion` — only constructible via `--from`), and things the TS lowerer never populates (constraints, descriptions, defaults, writeOnly, deprecated). **Each frontend uses a different subset of the shared model.**
4. The v2 client story is types-only by decision: openapi-typescript + openapi-fetch, no generated runtime validators. Runtime enforcement findings below are scoped to the servers.

---

## 1. Capability matrix — contract expressiveness (C# vs TS)

Verdicts: ✅ Full · ◐ Partial · ⚠ diagnosed-unsupported · ✖S **silent**-unsupported · 🐞 bug.

### 1.1 Bodies, files, wire formats

| Capability | C# | TS | Evidence / notes | Class |
|---|---|---|---|---|
| Binary **download** (typed file response) | ✅ `Define.File`, `.ProducesFile()`, `.ContentType()`, byte[]/Stream/`(byte[],string)` auto-detect (`ContractWalker.cs:313-344`) | ✅ `fileResponse` + `fileContentType` (lowerer:1576-1579) | Content-type fully controllable both sides. Default `application/octet-stream` (TS). | — |
| Binary **upload** (raw body) | ✖S — `Stream` input → `{}` schema; `byte[]` input → JSON *integer array* (probe `/api/raw/bytes`) | ⚠(misleading) — `input: Blob` → late `TYPE_NOT_FOUND "Blob"` far from the cause | No raw-body surface on either side. Uploads exist only via multipart. | C#: **c**; TS: **b** |
| Range requests / 206 / `Accept-Ranges` | ✖S — zero hits for ETag/Range/206-headers in Tool+Attributes | ✖ inexpressible (loud TS error if attempted) | Notable because `.QueryAuth()` is marketed for media players, where Range is table stakes. | **c/d** |
| multipart: file + scalar fields + nested DTO | ◐ works, but **no OpenAPI `encoding` map is ever emitted** — nested-DTO part content-type undefined for consumers | 🐞 contract OK, but **OpenAPI requestBody `$ref`s a schema that is never emitted** (dangling ref; see §3 BUG-2) | C#: `ContractWalker.cs:524-558`; TS: lowerer:2191-2282 + `OpenApiEmitter.cs:295-301` no existence check | C#: **c** (encoding); TS: **a** |
| multipart: multiple files | 🐞 `List<IFormFile>` works only beside a direct `IFormFile` prop; a record whose **only** files are `List<IFormFile>` emits as `application/json` with `format:binary` strings — unimplementable spec, zero diagnostic (`HasFormFileProperty`, `ContractWalker.cs:651-654`) | ⚠ exactly one Blob/File enforced; `Blob[]` → diagnostic (wrong message: "found none") | | C#: **a**; TS: **b** |
| multipart: optional file | ◐ `IFormFile?` works (named record); `.AcceptsFile()` file always required (`OpenApiEmitter.cs:321-324`) | ✅ `file?: Blob` → `isOptional:true` | | C#: **b-ish** |
| `acceptsFile` on GET | n/a | 🐞 guard only runs in the body-method branch (lowerer:2079); the Blob becomes a **query parameter** + stray `TYPE_NOT_FOUND` | | **a** |
| `application/x-www-form-urlencoded` | ✅ `.FormEncoded()` (`EndpointBuilder.cs:124`) | ✅ `formEncoded: true` — but nested objects accepted **silently** with undefined encoding semantics; `formEncoded`+`acceptsFile` together accepted silently (contradictory contract) | TS edge cases probed in `/tmp/rivet-audit-ts/p4` | TS edges: **c** |
| SSE / NDJSON / chunked | ◐ hack: `Define.File(...).ContentType("text/event-stream")` — payload untyped `format:binary` | ✖ inexpressible (no key; methods GET/POST/PUT/PATCH/DELETE only) | No event/item schema either side. | **d** (document) |
| WebSockets | ✖S — zero hits, no diagnostic | ✖ inexpressible | | **d** (document) |

### 1.2 Headers, cookies, auth

| Capability | C# | TS | Notes | Class |
|---|---|---|---|---|
| Typed request headers | ✖ — `ParamSource` = Route/Body/Query/File/FormField only (`TsEndpointDefinition.cs:48-55`); annotation path `[FromHeader]` → **diagnosed + dropped** (`EndpointWalker.cs:408-411`) | ✖ loud TS excess-property error | Headers are not a contract concept anywhere in Rivet. Importer relocates them to query with a marker. | **b/d** |
| Typed response headers (per status) | ✖S — `TsResponseType` has no headers field | ✖ same model | No ETag, `Location` for 201, rate-limit headers, pagination headers. | **c** |
| Cookies (params / Set-Cookie) | ◐ only as a global `cookie:<name>` security scheme (`SecurityConfig.cs:36-42`) | ✖ | | **d** |
| Auth schemes | ◐ one **global** scheme per run (`--security`): bearer / bearer:fmt / cookie / apikey. No OAuth2, OIDC, scopes, or AND/OR combinations. Undefined `.Secure("x")` → diagnosed but **fabricates a bearer** component (`OpenApiEmitter.cs:149-157`) — possibly the wrong type | ◐ `security:{scheme:"name"}` → name only, **hardwired http-bearer**; the vite plugin never passes `--security` at all, so TS users can't even reach the C# CLI's options | `.QueryAuth` emits a plain required query param + `x-rivet-query-auth`, not a securityScheme — defensible, but undocumented as such. | **b/c** |

### 1.3 Type system

| Capability | C# | TS | Class |
|---|---|---|---|
| Generic envelopes (`PagedResult<T>`), nested | ✅ monomorphised, `x-rivet-generic`, fixpoint (`OpenApiEmitter.cs:1014-1115`) | ✅ (monomorphisation happens in the .NET emitter, not the lowerer) | — |
| Recursive / mutually recursive types | ✅ (`TypeWalker.cs:250,264`) | ✅ | — |
| Polymorphism / discriminated unions | ✖S — `[JsonPolymorphic]`/`[JsonDerivedType]` **never read**; probe `Shape` flattened to base props, derived schemas vanish. The emitter has full `oneOf`+`discriminator`+mapping support (`OpenApiEmitter.cs:729-770`) that **only TS contracts can reach** | ✅ tagged unions with strict, well-diagnosed rules (single shared required string-literal tag, no optional props in variants, lowerer:3221-3346) | C#: **c** (worst type-system gap) |
| Dictionaries, constrained keys | ✖S — **key type arg ignored** (`TypeWalker.cs:502-505`); `Dictionary<Color,int>` → unconstrained string keys **and the `Color` enum schema vanishes from the spec entirely** | ◐ `Record<string,T>` only; literal-union/branded keys → `UNSUPPORTED_RECORD_KEY` | C#: **c**; TS: **b** |
| Enums | ◐ always camelCase **string** union; enum-as-int serializers undiagnosed; `[JsonConverter]` not inspected | ◐ string-only or int-only TS enums; literal unions ✅ (incl. `1\|2\|3` → int enum — inexpressible in C#); mixed → diagnosed | C#: **c** for int-enum APIs |
| Dates / times / UUID / durations | ✅ Guid→uuid, DateTime→date-time, DateOnly/TimeOnly→date/time — **except `TimeSpan` → `{}` empty schema** with a warning that misnames the cause | ◐ `Date`→date-time; everything else via manual `Format<string,"uuid"\|"date"\|"duration">`; arbitrary format strings accepted unvalidated | C# TimeSpan: **c** |
| Big numbers | ◐ decimal→`number/decimal`; long→int64 (no JS-precision caveat); **`BigInteger`/`char`/`object` → `{}` empty schema** | ⚠ `bigint` → diagnosed | C#: **c** |
| `byte[]` property | 🐞 spec says integer array; **STJ puts base64 strings on the wire** — contract/wire divergence in every consumer; falsifies "maps what actually survives the wire boundary" | n/a | **a** |
| Integer fidelity | ✅ int/long → integer+format+bounds | ✖ — `RivetPrimitiveTypeName` has no integer; `number` → `{type:"number"}` (proved in parity diff) | TS: **c** (silent precision loss) |
| Nullable vs optional (all four quadrants) | ✅ `[RivetOptional]`, `T?`, `[Required]+T?` all correct | ✅ `?` / `\|undefined` / `\|null` — except optional props in *inline* objects and union variants → diagnosed | — |
| Tuples | ✅ inline object {key,value} (a design choice, not 3.1 `prefixItems`) | ⚠ diagnosed | — |
| Intersections / mapped / conditional / template-literal types | n/a | ⚠ all diagnosed (`UNSUPPORTED_TYPE_EXPRESSION`) — the exact-shape authoring types make almost everything fail loudly | TS diagnostics are a genuine strength |
| Brands | ✅ single-prop record → `x-rivet-brand` (Roslyn path) | 🐞 lowered fine, but the `--from` path drops the brands dictionary → **dangling `$ref` in OpenAPI** (§3 BUG-1) | TS: **a** |
| Inline anonymous object types | ✖ named records only | ✅ `inlineObject` | — |

### 1.4 Validation metadata

| | C# | TS |
|---|---|---|
| Into OpenAPI | ✅ `[MinLength]` `[MaxLength]` `[StringLength]` `[Range]` `[RegularExpression]` `[EmailAddress]` `[Url]` + `[RivetConstraints]` (exclusive bounds, multipleOf, min/maxItems, uniqueItems), merged correctly (`TypeWalker.cs:657-728` → `SchemaEnricher.cs:65-78`) | ✖ **nothing** — no constraints API, no JSDoc reading (zero `getJSDocTags` calls in the 3,465-line lowerer). The contract-JSON schema *has* a constraints field; the lowerer never emits it. Property-level descriptions also never extracted. **Class: c — the biggest cross-side asymmetry.** |
| Silently vanishing (C#) | `[Compare]`, `[CreditCard]`, `[Phone]`, `[Base64String]`, custom `ValidationAttribute`s, and **FluentValidation rules** — the validation mechanism the codebase itself uses. Class: **c**. | |
| Runtime-enforced | **Neither side enforces any of it at runtime** (§4). Constraints are documentation-only everywhere. | same |

### 1.5 Per-endpoint / per-contract concerns

| Capability | C# | TS | Class |
|---|---|---|---|
| Multiple success statuses | ✅ `.Returns<T>(code)` accepts 2xx; probe emitted 200+201+204+409 distinct schemas; runtime-enforced | ◐ hack: put 2xx entries in `errors:` — works but semantically mislabeled | — |
| `successStatus` | ✅ | 🐞 **non-literal value silently ignored** → default status, no diagnostic (`readNumericLiteral` returns null); same pattern for non-literal `summary`/`description` | **c** |
| Endpoint deprecation | ✖S — no `.Deprecated()`, `[Obsolete]` on endpoints not read (property-level works) | ✖ no key (loud if attempted) | **c**/b |
| API title/version/servers | ✖ hardcoded `"API"`/`"1.0.0"`, no servers, no CLI flags (`OpenApiEmitter.cs:84-89`) | same emitter | **c** — every spec from every project is "API v1.0.0"; Redocly errors on both counts |
| operationId / tag override | ✖ convention only (`{controller}_{name}`) | ✖ same | **d** |
| Caching / ETag / 304 | ◐ `.Returns(304)` declares the status; no headers ⇒ no actual caching contract | ✖ | **c/d** |
| Request/response examples | ✅ rich: JSON/Ref builder methods, `[RivetExample]`, component examples, undeclared-status → diagnosed | ✅ typed request examples (assignability-checked!), status-scoped response examples — but response examples **not** type-checked, and examples never validated against media-type schema (§3 BUG-3) | ◐ |
| Diagnostics infrastructure | free-text stderr warnings, **no IDs**, exit 0 on almost everything; the catch-all "'unknown' type" warning (`OpenApiEmitter.cs:788`) names no symbol and blames JsonElement — fires for TimeSpan/BigInteger/char/object/Stream | 48 stable diagnostic codes + full tsc pre-emit; CLI exits 1 on error (but still writes the partial artifact) | C#: needs IDs |

---

## 2. The OpenAPI surface — importer fidelity (golden-contract test)

Ran `--from-openapi` against 5 corpus specs. All exited 0; scaffolded C# compiles (verified petstore, kubernetes).

| Spec | Ops | Imported | Warnings | Headline |
|---|---|---|---|---|
| petstore-v3 | 19 | 19 | 0 | full round-trip done (below) |
| notion | 13 | 13 | 0 | `Notion-Version` header → query param on re-emit — **breaks the real API** |
| github | 1099 | 1099 | 343 | secondary media types (`vnd.github.object`, `.diff`, `.patch`) silently dropped |
| kubernetes | 248 | **236** | 0 | **12 HEAD/OPTIONS ops silently dropped, zero diagnostics**; PATCH media types diagnosed-dropped |
| stripe | 587 | 587 | 1074 | **262 GET ops silently lost ALL path+query params** |

### Importer gap register

| Finding | Evidence | Class |
|---|---|---|
| **Parameters discarded whenever the op has a request body** — `ResolveParamInputType` only runs `if (inputType is null)` | `ContractBuilder.cs:91-94`; Stripe `GET /v1/quotes/{quote}/pdf` lost its path param; 262 ops affected, zero diagnostics | **a** — directly violates the documented "nothing is dropped silently" |
| HEAD/OPTIONS/TRACE skipped without warning | `ContractBuilder.cs:30-33` | **c** |
| Query params relocated into the JSON body on non-GET ops, undiagnosed | petstore round-trip: `updatePetWithForm` query `name`,`status` re-emit as JSON body properties | **a** |
| Security scheme **type** erased at import (only the name survives) — oauth2+apiKey round-trip as `http bearer` | `OpenApiImporter.cs:162-185` (components.securitySchemes never read) | **c** |
| "error status preserved untyped" doc claim — marker added, error response **not** added; status dropped unless an example resurrects it | `ContractBuilder.cs:467-470` vs `:526-568` | **a** (doc) / **c** (code) |
| Secondary content types dropped when one supported type matches | `ContractBuilder.cs:152-164, 375-380` | **c** |
| Param `default` values lost; params with `content` instead of `schema` skipped silently | `ContractBuilder.cs:211-214, 295-300` | **c** |
| webhooks, callbacks, links, response headers, servers, param style/explode, discriminator mapping, OAuth scopes, 1xx/3XX | `OpenApiImporter.cs:26-38` reads only schemas+security+paths | **d** — documented |

**What's genuinely solid:** allOf inheritance (incl. required-tightening), oneOf/anyOf wrappers, 3.0-nullable and 3.1 type-array handling, $ref alias cycles, enum import incl. `x-enum-varnames`, constraint → DataAnnotations mapping, the documented warning categories match the test ratchet exactly, and the diagnostic ratchet (`ImportMetricTests.cs:48-68`) is real engineering.

**Import-profile doc accuracy:** the warning taxonomy is accurate; the headline "nothing is dropped silently" is false on at least three counts (params-with-body, HEAD/OPTIONS, secondary media types); "path and query parameters → synthesized input records" needs the no-body caveat; "one scheme imports" should read "one scheme **name** imports".

---

## 3. The OpenAPI surface — emission, tested against real consumers

Emitted specs from samples/ContractApi, TypeShowcase (Roslyn path) and expressive/smoke/myapp fixtures (TS `--from` path); ran Redocly, Spectral, Prism, openapi-zod-client, swagger-typescript-api, openapi-typescript.

**Roslyn-path specs work**: Prism boots and serves mocks; zod-client and swagger-typescript-api generate cleanly; lint findings are style-level plus the structural gaps below.

**TS-path specs are broken for real consumers** by two dangling-`$ref` bugs:

- **BUG-1 (a):** `--from` drops the brands dictionary (`Program.cs:121-134`, `JsonContractReader.cs:19-36`) while `MapTsTypeToJsonSchema` still emits `$ref: …/{brand}` (`OpenApiEmitter.cs:686-689`). Any TS contract using `Brand<>` → **Prism won't boot, openapi-zod-client and openapi-typescript fail, Spectral crashes**, swagger-typescript-api silently degrades the field to `any`. These are the exact tools the README points users at.
- **BUG-2 (a):** every TS multipart endpoint emits `$ref: …/{inputTypeName}` for a schema nobody defines — the lowerer decomposes the input into params and never ships the type (lowerer:363-374, 1654-1660); the emitter refs it with no existence check (`OpenApiEmitter.cs:295-301`), despite already having the E6 fallback pattern for exactly this situation in generics (`:1080-1095`). The multipart smoke test only asserts the content key exists, so it never caught this.
- **BUG-3 (c):** examples are attached without validation against the media type/schema (`WithExamples`, `OpenApiEmitter.cs:555-624`) — a JSON-object example on a `text/csv` binary response emits and Spectral rejects it. No diagnostic in either repo.
- **Minor (b):** the TS CLI writes the output artifact even on error exit (run-cli.ts:141-151) — exit code is loud, the golden-file overwrite is silent.

**Never emitted** (single emitter ⇒ applies to both sides): servers; info beyond hardcoded "API/1.0.0"; tag descriptions; response headers; header/cookie parameters; operation-level `deprecated`; OAuth2/OIDC/scopes/mutualTLS; multiple content types per response; `additionalProperties: false` (everything is an open object — zod output is `.passthrough()` everywhere); `default` responses; webhooks/callbacks/links/externalDocs; 3.1 `const`/`prefixItems`/`contentMediaType`. The 3.1 it does emit is idiomatically correct (type arrays, `examples` keyword, discriminator mapping, readOnly/writeOnly, schema-valued additionalProperties). **Emission is deterministic — byte-identical across runs.** Files use the 3.0 idiom `format: binary`; all tested consumers tolerated it.

**TS-pipeline metadata poverty (c, large):** the TS lowerer emits only `name/type/optional/readOnly` per property; the shared model supports constraints (11 kinds), description, default, example, writeOnly, deprecated — all populated by Roslyn from XML docs + DataAnnotations, none by TS. TS-sourced specs are silently weaker than C#-sourced ones through the *same* emitter.

---

## 4. Runtime enforcement honesty

### 4.1 .NET (`TypedResultValidator`)

Enforcement is **production-on, unconditional** (no env gates; only per-endpoint `.SkipValidation()`), but only on the typed-results `Invoke<T...>` overloads (`EndpointBuilder.cs:335-575`). It checks exactly two things: declared status code, and CLR payload type via `IValueHttpResult<T>` + `IsAssignableFrom`. It never inspects serialized JSON, content-type, or headers. Failures throw `InvalidOperationException` → **empty-body 500 in production** (no envelope), text/plain stack trace in Development.

| Violation | Caught? | Proof |
|---|---|---|
| Wrong / undeclared status | ✅ | curl → 500 (`TypedResultValidator.cs:80`) |
| Wrong payload CLR type, anonymous object, raw `Results.Json` escape | ✅ | curl → 500 (`:112`, `:28`) |
| JSON body where 204 declared; empty where body declared | ✅ | `:97`, `:105-110` |
| File where JSON declared | ✅ *by accident* (`FileContentHttpResult` lacks `IStatusCodeHttpResult`) | `:22-26` |
| **Extra fields via derived type** (even upcast to `Ok<Declared>`) | **✖ — 200 with `secret`/`isAdmin` on the wire** | curl-proved; `IsAssignableFrom` + STJ runtime-type serialization |
| **Null body / null required fields** (right CLR type) | ✖ — 200 | type-level only |
| **Wrong content-type** (right type+status, e.g. `text/csv`) | ✖ | no content-type code exists |
| Body on void contract w/ matching status | ✖ — 200 text/plain leaked | hole between `:93-102`/`:134-139` |
| **Inbound constraint violations** (`[Range]`, `[MinLength]`, `[RivetConstraints]`) | **✖ — 201 success** | `RivetConstraintsAttribute` is not a `ValidationAttribute` (`RivetConstraintsAttribute.cs:8`); repo's own test admits it (`ValidationIntegrationTests.cs:105-110`) |
| `RivetResult<T>` plain-`Invoke` path | validator **never runs** — compile-time generics + user-written bridge only | `EndpointBuilder.cs:260-265` |
| **`Define.File` endpoints** | **zero enforcement** — no `Invoke` exists; JSON served on an image/jpeg contract → 200 | `EndpointBuilder.cs:586-624` |
| Contract↔route binding | none — route strings duplicated by hand in `MapGet`/controllers; nothing forces handlers through `Invoke` | structural |

### 4.2 Hono adapter (`rivet-ts/src/hono.ts`)

**Outbound validation does not exist** — `context.json(result)` verbatim (`hono.ts:408`). No dev/prod gate; it's simply absent. The only outbound check is the file-endpoint binary-type gate (`:369-389`) + forced file content-type (`:401-405`).

| Violation | Caught? | Proof |
|---|---|---|
| Inbound: NaN number, bad boolean, missing required param, repeated scalar, malformed JSON, missing multipart field | ✅ → structured 400 (`INVALID_PARAMETER_VALUE` etc., `hono.ts:178-331`) | curl-proved |
| Inbound: **request body shape** (wrong types, extra fields, array/null at root) | **✖ — `req.json()` passed verbatim**; handler receives anything, typed as the DTO | curl → 201 |
| Inbound: enum query values | ✖ — raw string passes (comment at `hono.ts:191` admits it) | curl → 200 |
| Inbound: headers | not a contract concept | — |
| Outbound: missing required field / extra fields (incl. secrets, **no cast needed** — structural typing via variable assignment) / wrong types | **✖ — all 200** | curl-proved + tsc-proved no-cast leak |
| Outbound: raw `Response` returned | ✖ — serialized as `{}` with **200**: handler's status and body silently destroyed | curl-proved (`hono.ts:408`) |
| Outbound: `undefined` where body declared | ✖ — empty 200 | `:397-399` |
| Outbound: undeclared error status (`rivetHttpError(418,…)`) | ✖ — contract `errors` list is decorative at runtime | `:551-564` |
| Wrong success status | n/a by design — status fixed from contract (`:340-357`) | good design |
| Body-forbidding status with data | ✅ constructor throws | `:426-432` |

### 4.3 Cross-runtime consistency — they disagree on nearly everything

| Dimension | .NET | Hono |
|---|---|---|
| Outbound status enforcement | ✅ runtime | n/a (fixed by adapter — different but defensible) |
| Outbound type/shape | CLR type only | **nothing** |
| Extra-field leakage | **leaks** | **leaks** (the one consistent behavior, and it's the worst one) |
| Inbound constraint enforcement | none | none |
| Inbound scalar coercion | ASP.NET model binding (400, empty body) | hand-rolled `Number()`/literal-boolean (400, JSON envelope); note `Number("0x10")`→16, `"1e3"`→1000 |
| Enforcement-failure envelope | **empty-body 500**, no shape | structured `{code,message}` **400** |
| Wrong-method escape hatches | `RivetResult` path, `.SkipValidation`, `Define.File` | plain `Response` return, `rivetHttpError(anyStatus)` |

**Verdict on the core promise:** what's actually guaranteed cross-stack is *status-code conformance on the .NET typed path* and *param presence/scalar shape on Hono inbound*. Body shape, extra fields, content-type, headers, and every declared constraint are unenforced on both sides. The published constraint metadata (which C# faithfully emits to OpenAPI) is **never checked by either server** — consumers can legitimately validate stricter than the producer. Class: **c** across the board, because nothing in the docs scopes the promise this narrowly.

(Client-side runtime validation is types-only **by decision** — the v2 client story is openapi-typescript + openapi-fetch, and `docs/guides/runtime-validation.md` already states "Rivet itself does not generate validators". Not a gap; out of scope for this register.)

---

## 5. Cross-repo & lifecycle

### 5.1 Dev loop (proved by executing scaffold → vite dev → edit → break → recover)

| Finding | Evidence | Class |
|---|---|---|
| `emitClientPackage` **silently falls back to the scaffold-time bootstrap spec** when `openapi.json` is missing or stale — no error logged, dev server keeps serving, `schema.d.ts` quietly stops reflecting the contract. Should fail loudly. | `vite.ts:139-146` + observed live | **c** |
| Documented `pnpm --dir packages/api run generate` → `rivet: command not found`, exit 127 (script calls bare `rivet`; nothing puts the cached binary on PATH) | `mock-project-emitter.ts:800` | **a** |
| Fresh scaffold fails its own typecheck when the contract uses TS enums or `Format<>` (mock generator emits raw literals: TS2322 day one) | observed | **a** |
| Broken contract mid-edit: clear file:line error, last-good artifacts persist, no reload, instant recovery on fix; `vite build` fails loudly | `vite.ts:110-131, 200-206` | ✅ by design, good |
| Transitive-dep change during regen dropped (`watchedFiles.clear()` inside async regen; `has()` guard misses files changed in the window) → silently missed regen until next entry touch | `vite.ts:174, 213` | **a** (small race, real) |
| No debounce — one save = two full regen + full-reload cycles; promise queue prevents interleaving | `vite.ts:160-184` | b/minor |
| Scaffold emits **no .gitignore**; `generated/` unignored → committed-then-stale artifacts one `git add .` away; no hash/staleness detection anywhere | observed | **c** |
| `vite dev` never typechecks; manual `pnpm run check` can read mid-write `generated/` | plain `fs.writeFile` sequencing | minor |
| rivet-ts has **no test/typecheck CI** (docs workflow only); rivet runs tests only on version tags, with sample-exercising tests excluded (`Category!=Local`) | `.github/workflows/` | **c** for a contract tool |
| .NET side: no drift detection for downstream consumers who forget to rerun `dotnet rivet` (no hash, no CI check); `samples/ContractApi/README.md:15-24` documents the pre-rewrite CLI | observed | **c** / doc rot |

### 5.2 C# ↔ TS parity (same API authored both sides, diffed)

Near-identical output — single-emitter design pays off. Only two semantic deltas: **TS cannot express integers** (`number` → `type:"number"` vs C# int32+bounds), and C# emits an orphan input-record schema TS prunes. The full asymmetry inventory is §1; the headline asymmetries: C# alone has validation constraints, property metadata, annotation mode, import, runtime output validation, coverage checker; TS alone has tagged unions, literal unions, inline objects, explicit `params`/`query` blocks, int-literal enums.

### 5.3 PHP sidecar

`php-reflector/` is a **dead husk** (.idea + stale phpunit cache). The code moved to its own repo (commit `924fc4c`), alive at `~/Sites/medway/rivet-php` — a contract-JSON producer for the same `--from` pipeline. Stale claims: `rivet-php/composer.json` `support.source` still points at the gutted in-repo path (FABLE_REVIEW PHP1, never actioned); `docs/php-limitations.md` is an orphan stub. Per the repo's own prior review, the standalone path works but the Laravel/Symfony adapter has known fidelity holes (dates, framework-param filtering) that its README overclaims. **Action: delete the directory, fix the composer pointer.**

---

## 6. Marketing claims vs reality (summary)

| Claim | Verdict |
|---|---|
| "no drift between what your API does and what the spec says" | **False** for: byte[] (int-array vs base64), polymorphism (derived types vanish), enum-as-int serializers, dictionary keys, FluentValidation rules — all silent |
| "faithful OpenAPI 3.1" | 3.1 idioms genuinely correct; "faithful" undermined by hardcoded info, no servers, and the TS-path dangling refs |
| "losslessly re-importable" (x-rivet-*) | Mostly holds for the Roslyn happy path; round-trip loses security scheme types, header params, query-param location (non-GET), param defaults |
| Importer: "nothing is dropped silently" | **False** (params-with-body ×262 on Stripe, HEAD/OPTIONS, secondary media types) |
| "plugs straight into openapi-typescript / openapi-fetch / openapi-zod-client" | True for C#-authored contracts; **false for TS contracts using brands or multipart** (the recommended tools crash) |
| Runtime enforcement (implied by TypedResultValidator / typed handlers) | Real but narrow: status + CLR type (.NET typed path only); zero outbound validation on Hono; constraints never enforced anywhere |

---

## 7. What to build next (prioritized)

**P0 — the promise-breakers (all bugs, all currently silent):**
1. **Scaffold/dev-loop hygiene** (§5.1): make `emitClientPackage` fail loudly when `openapi.json` is missing/stale instead of silently re-reading the bootstrap spec; fix the scaffold `generate` script (exit 127); fix the enum/`Format<>` mock-literal TS2322s so a fresh scaffold passes its own typecheck.
2. **Fix the two dangling-`$ref` emitters for TS contracts** (brands: thread the dictionary through `JsonContractReader`/`Program.cs`; multipart: emitter-side existence check + inline fallback, mirroring the existing E6 generic fallback). These break Prism/zod-client/openapi-typescript on the documented happy path. Add a spec-resolves lint (Redocly or `$ref` walk) to the test suite so this class can't recur.
3. **Importer: stop silently discarding parameters on operations with bodies** (`ContractBuilder.cs:91-94`) — merge params with the body input or at minimum emit a marker; warn on skipped HEAD/OPTIONS/TRACE. This is what stands between "golden contracts from Stripe" and quiet data loss on 45% of its GETs.

**P1 — make the enforcement story honest (pick: build it or scope it in writing):**
4. **Outbound body validation** from the contract schema — extra-field stripping/rejection and required/type checks — for both runtimes (Hono has nothing; .NET has CLR-type-only). The extra-field leak is a data-exfiltration primitive on both stacks today. If full schema validation is too costly, ship extra-field stripping + a documented scope statement.
5. **Inbound constraint enforcement** — the contract advertises `[Range]`/`[RivetConstraints]` to every consumer but neither server checks them. Either enforce (make `RivetConstraintsAttribute` a `ValidationAttribute` + run DataAnnotations; generate a Hono inbound validator from the contract) or document loudly that constraints are spec-only. Also: Hono request-body schema validation (today `req.json()` is passed verbatim, root `null`/array included) and enum-param checking.
6. **Close the .NET enforcement escape hatches**: `Define.File` endpoints (no Invoke at all), body-on-void leak, content-type check, and document that the `RivetResult` path is compile-time-only.
7. **Align the two runtimes' failure envelopes**: .NET's empty-body 500 vs Hono's structured 400 `{code,message}` — pick one envelope and emit it from both (and put it in the spec as a default response).

**P2 — highest-value expressiveness gaps (each currently silent):**
8. ~~TS validation/metadata channel~~ — **deferred by decision (2026-06-11): rivet-ts is a plaything; parity with .NET only matters if it's ever used seriously.** (Was: constraints/descriptions/defaults/writeOnly/deprecated — the shared model and emitter already support all of it; only the lowerer is missing. Same deferral applies to the TS integer type / `RivetPrimitiveTypeName` gap.)
10. **C# polymorphism**: read `[JsonPolymorphic]`/`[JsonDerivedType]` → the `TaggedUnion` model the emitter already renders. Today derived schemas silently vanish.
11. **Headers as contract concepts** (request params + per-status response headers) — unlocks ETag/caching, `Location` on 201, pagination/rate-limit headers, and fixes the importer's header→query mangling (Notion's `Notion-Version`).
12. **C# silent type-system holes**: byte[]→base64 string, dictionary key types (+ vanishing key-enum schemas), `List<IFormFile>`-only multipart detection, TimeSpan/BigInteger/char → diagnosed instead of `{}`; give every C# warning a stable ID and make the "unknown type" warning name the offending symbol.
13. **info/servers plumbing** — `--title/--version/--server` flags + vite-plugin/scaffold passthrough (also `--security` from the plugin). Trivial, removes the two lint errors every consumer sees.

**P3 — hygiene and documentation:**
14. Scaffold .gitignore for `generated/`, debounce in the vite plugin, fix the `watchedFiles` race, stop writing artifacts on error exit (TS CLI), drift-detection story for .NET consumers (hash or CI check), CI that runs the test suites (rivet-ts has none).
15. Documentation debt: scope statements for WebSockets/SSE/Range (class-d items), correct the import-profile over-claims (§2), fix `samples/ContractApi/README` CLI docs, delete `php-reflector/` and fix the rivet-php composer pointer, document `.QueryAuth`'s emitted-as-parameter semantics and the security-scheme fabrication behavior.

---

*Scratch artifacts retained under `/tmp/rivet-audit-{cs,ts,import,emit,dotnet-rt,hono,xrepo}/` for reproduction.*
