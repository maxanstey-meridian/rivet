# Import profile (`--from-openapi`)

`--from-openapi` is an **onboarding scaffold**, not a sync tool. Run it once to
bring an existing OpenAPI 3.x API into Rivet: it generates C# contracts + DTOs,
you review the diagnostics below, fix what matters, and from that point the C#
is the source of truth — the forward pipeline (`--openapi`) regenerates the spec
from your code. Re-running the import against a drifted spec is supported but is
a re-onboarding, not an incremental merge.

The importer's contract: **nothing is dropped silently, with one known
exception.** Anything the C# contract model cannot represent either becomes a
structured marker comment in the scaffolded code or a named warning on stderr.
The exception: security scheme **types** are erased without a diagnostic — only
the scheme *name* survives the import (see Security below).

## What imports cleanly

- **Operations**: GET/POST/PUT/PATCH/DELETE; `operationId`/tag naming (or the
  `x-rivet-contract`/`x-rivet-endpoint` extensions when present); summaries,
  descriptions, deprecation. HEAD/OPTIONS/TRACE operations have no contract
  representation and are dropped — each one with a named warning
  (`RIV3003` / `operation-method-dropped`, see below).
- **Bodies**: `application/json` (typed records), `application/x-www-form-urlencoded`
  (form-encoded inputs), `multipart/form-data` (incl. `IFormFile` /
  `List<IFormFile>`), binary content types (file endpoints / `ProducesFile`),
  `text/*` and `*/*` fallbacks. A `text/*` body or success response keeps its
  media type via `.AcceptsContentType(...)` / `.ProducesContentType(...)` — it
  no longer re-emits as `application/json`. An **optional** body
  (`required: false`, the OpenAPI default) imports as a nullable `TInput`
  (`.Accepts<T?>()`) and re-emits `required: false` — except when path/query
  params merged into the body record, where whole-input nullability would be
  a lie; that case keeps `required: true` with a marker
  (`body-optionality … optional-body-merged-with-required-params`).
- **Property names**: spec keys PascalCase into C# members; whenever
  `camelCase(member)` is not the original key (snake_case keys,
  already-PascalCase keys), the original is pinned with
  `[JsonPropertyName("original_key")]` so neither the runtime serializer nor
  the re-emitted spec drift. Keys that PascalCase into reserved record
  machinery (`Equals`, `ToString`, `GetHashCode`, `GetType`, `Deconstruct`,
  `EqualityContract`) are renamed with a `Value` suffix — the pin keeps the
  wire name intact. Schema names that collide case-insensitively after
  sanitization are numeric-suffixed (emitted files live on case-insensitive
  filesystems).
- **Undiscriminated `oneOf`** (no discriminator — e.g. `string | integer`):
  imported as an `As*` wrapper record carrying `[RivetUnion]`. The attribute
  doubles as a `JsonConverter`: the wire value is the BARE variant, and the
  walker re-emits the wrapper as a plain `oneOf` — round-trip faithful.
  A `{"type": "null"}` variant degrades to a permissive empty schema.
- **Example values**: embedded `{"$ref": "#/components/examples/X"}` example
  VALUES (the github anti-pattern) are inlined at import time, while the
  source components are in hand — a round-trip can never dangle them.
  Unresolvable refs degrade loudly (`unresolvable-embedded-example-ref`).
- **Parameters**: path, query and header parameters → synthesized input records.
  Header parameters KEEP their location (P2 wave 5): the synthesized property
  carries `[RivetHeader("Original-Name")]` — original casing included — and
  re-emits as `in: header`. Cookie parameters are still folded in with their
  location erased (see the markers below).
  On operations that **also carry a request body**, parameters are merged with
  the body-derived record into a single synthesized `{Field}Input` record:
  an identically named *and* typed body property collapses into the parameter;
  a body property with the same name but a different type is shadowed by the
  parameter, with a marker. When the body type is not a plain record
  (primitive, collection, generic, union wrapper, `JsonElement`, …) a merge is
  structurally impossible — each parameter is then dropped, with a marker per
  parameter. On body-carrying methods (POST/PUT/PATCH) a merged *query*
  parameter's location is erased: it re-emits inside the JSON body (marked per
  parameter; path parameters keep working via the route template).
- **Schemas**: named objects → sealed records; `allOf` inheritance chains —
  including middle layers' own properties and top-level `required` tightening;
  `oneOf`/`anyOf` → `As*` wrapper union records; string/int enums; single-value
  branded primitives (`x-rivet-brand`); generics (`x-rivet-generic`);
  dictionaries via `additionalProperties`; `$ref` aliases (resolved to targets).
- **Nullability and requiredness — independent axes, both preserved.**
  3.0 (`nullable: true`) and 3.1 (`type` arrays, null branches) forms import on
  inline properties AND on components: a `$ref` to a component that is itself
  nullable resolves nullable at every use-site. A property that is required
  AND nullable scaffolds in the non-positional form with the C# `required`
  keyword (`public required T? X { get; init; }`) — must be present, may be
  null — and re-emits with both axes intact. Optional non-nullable properties
  still WIDEN to nullable on import (`T?` is the only optionality spelling on
  positional records) — an under-claim, the one residual conflation. A null
  branch inside a 3+-variant `oneOf` union is also still dropped (the `As*`
  wrapper has no nullability slot; the `{"type": "null"}` degradation marker
  covers the 2-variant case).
- **Enum wire values**: pinned with `[JsonStringEnumMemberName]` whenever the
  emitted value (`camelCase(member)`) differs from the original — `Ready`,
  `COLLABORATOR` and `EastUs` survive exactly, not case-mangled.
- **Responses**: lowest concrete 2xx wins (a `2XX` wildcard maps to 200 when no
  concrete 2xx exists); typed error responses; `default` → 500; `4XX`/`5XX` →
  400/500 — every range projection to a literal status carries a loud
  `status-range` marker, since the spec never promised that exact code; named
  and `$ref` component examples. An operation with no 2xx at all declares its
  lowest non-error status via `.Status(...)` — 3xx redirects and 1xx
  informational (websocket `101`) alike, no fabricated `200`. A 1xx beside a
  concrete 2xx has no contract axis and drops with a marker. JSON `null`
  example values import as `null` (the Microsoft.OpenApi sentinel string is
  converted back at import).
- **Response headers** (P2 wave 5): re-emitted as `.WithResponseHeader(status,
  "Name", description, required:)` chain calls — name, description and
  `required` survive. The header schema is string-typed in v1: a non-string
  schema degrades to `string` with a marker. Headers on a status the contract
  cannot declare (e.g. a second 2xx) are dropped with a marker.
- **Validation metadata on schema properties**: min/max length, pattern,
  ranges, item counts → DataAnnotations / Rivet constraint attributes;
  defaults, formats, descriptions, examples, readOnly/writeOnly. Records
  whose properties carry a `ValidationAttribute` are scaffolded as
  non-positional `required`/`init` records — the MVC-safe placement (see the
  [positional-record gotcha](/guides/runtime-validation#the-positional-record-gotcha));
  unconstrained records stay positional.
- **Security**: one global scheme **name** and one scheme **name** per
  operation (`.Secure(...)`), empty `security: []` → `.Anonymous()`. The scheme
  **type** (apiKey / http bearer / oauth2 / …) is erased on import: the contract
  model carries only the name, and the forward pipeline's re-emitted scheme
  definition comes from the `--security` flag, not from the original spec.

## What diagnoses

### Marker comments in scaffolded contracts — `// [rivet:unsupported …]`

| Marker | Meaning |
|---|---|
| `body $ref=… reason=unresolved-ref` | Request-body `$ref` could not be resolved; the input type was dropped. |
| `body content-type=…` | Request body declares only unsupported content types. |
| `response status=… content-type=…` | 2xx response content has no supported schema; endpoint imported as void. |
| `error status=… content-type=…` | Error response content has no supported schema; status preserved untyped. |
| `… reason=media-type-parameters` | Suffix on the three above: a media type carried parameters (e.g. `application/json; charset=utf-8`), which defeats the exact content-type match. |
| `request-example …` / `response-example …` (`reason=unresolved-ref` / `missing-value`) | An example could not be resolved/carried. |
| `security schemes=… reason=multi-scheme-first-only` | Operation declares multiple security schemes (OR alternatives / AND combinations); only the first is imported, scopes dropped. |
| `param name=… in=cookie reason=location-erased-to-query` | Cookie parameter folded into the input record; it will re-emit as a query parameter. (Header parameters stopped erasing in P2 wave 5 — they re-emit as `in: header`.) |
| `header name=… status=… reason=schema-degraded-to-string` | Response header declared a non-string schema; imported with a `string` schema (v1 response headers are string-typed). |
| `param name=… in=header reason=reserved-header-dropped` | `Accept`/`Content-Type`/`Authorization` declared as a header parameter — OpenAPI forbids these, the emitter could never re-emit them, so the parameter is dropped. |
| `header name=… status=… reason=undeclared-status` | Response header sits on a status the contract cannot declare (e.g. a non-lowest 2xx); the header was dropped. |
| `param-metadata params=… reason=metadata-dropped` | Parameter descriptions / deprecation / validation constraints did not survive into the synthesized input record. |
| `param name=… in=… reason=dropped-unmergeable-body body-type=…` | A body-carrying operation (POST/PUT/PATCH) has both parameters and a request body whose type is not a plain record — the body wins (TInput re-emits as the JSON body) and the parameter was dropped. |
| `body method=… reason=opaque-body-dropped-params-kept body-type=…` | A bodyless-method operation (GET/DELETE) has both parameters and a request body whose type is not a plain record. TInput lowers to route/query params, which an opaque type cannot do — so the parameters win and the body was dropped (its form-encoding/content-type metadata with it). |
| `response status-range=… projected=…` / `error status-range=… projected=…` | An OpenAPI status range (`2XX`/`4xx`/`5xx`) was projected to a literal status the spec never promised. |
| `response status=1xx reason=informational-status-dropped` | A 1xx response beside a concrete 2xx — the contract has no informational-status axis, so it was dropped. |
| `param name=… in=… reason=body-property-shadowed-by-param body-type=…` | A body property shares a parameter's name but not its type; the parameter won, the body property was dropped. |
| `param name=… in=query reason=location-erased-to-body` | Query parameter merged into a body-carrying operation's input record; it will re-emit inside the JSON body. |
| `body-location method=DELETE reason=body-lowered-to-query-params` | DELETE request body imported as `.Accepts<T>` — Rivet lowers DELETE inputs to query params, so the body's properties re-emit as required **query** params (never import a secret-carrying DELETE body silently; this marker is why). |
| `body-optionality required=false reason=optional-body-merged-with-required-params` | Optional request body merged with required path/query params into one input record — whole-input nullability would be a lie, so the body re-emits `required: true`. |

### Warnings (stderr / `ImportResult.Warnings`)

Each warning carries a stable `RIV3xxx` diagnostic ID (printed as
`warning RIV3001: <message>` on stderr, and as a `RIV3001: ` prefix on
`ImportResult.Warnings` — see the
[Diagnostics Reference](/reference/diagnostics)) and belongs to a named
category, ratcheted by the test suite (`ImportMetricTests.CategorizeWarning`,
keyed by ID) — new categories are added consciously, never absorbed:

| ID | Category | Trigger |
|---|---|---|
| `RIV3009` | `unresolved-schema` | Schema could not be resolved to a C# type → `JsonElement`. |
| `RIV3010` | `unsupported-schema-type` | Unhandled JSON Schema `type` → `JsonElement`. |
| `RIV3011` | `array-missing-items` | Array schema without `items` → `List<JsonElement>`. |
| `RIV3012` | `enum-constraint-dropped` | Enum that can't be a C# enum (single-value, mixed, out-of-range) degrades to a primitive. |
| `RIV3005` | `discriminator-dropped` | `discriminator` with no reversible polymorphic shape (plain object without `oneOf`, or `oneOf` whose `mapping` is absent/unusable) — dispatch semantics dropped. |
| `RIV3001`, `RIV3006`, `RIV3007`, `RIV3008` | `alias-unresolvable` | Cyclic / dangling `$ref` alias chains broken with placeholders. |
| `RIV3013` | `properties-dropped` | Schema declares both `properties` and `additionalProperties`; the dictionary side won (inline objects). |
| `RIV3004` | `additional-properties-dropped` | Same conflict on a named schema; the record side won. |
| `RIV3002` | `security-schemes-dropped` | Document-level `security` declares multiple schemes; only the first imported. |
| `RIV3003` | `operation-method-dropped` | HEAD/OPTIONS/TRACE operation dropped — the HTTP method has no contract representation. |

## Out of scope

These have no C# contract representation and are not imported (beyond the
diagnostics above where applicable):

- `callbacks`, `webhooks`, `links`
- Response-header schemas beyond `string` (name/description/required are
  imported; the schema type is not — see the markers above)
- Parameter serialization (`style`, `explode`, `allowReserved`), XML mappings
- Polymorphic `discriminator` dispatch *without* a usable `oneOf` mapping
  (imports as plain records/unions, loudly); usable mappings reverse to
  `[JsonPolymorphic]`/`[JsonDerivedType]` hierarchies
- Multi-scheme security semantics, security scheme type definitions, OAuth
  scopes/flows
- `servers` (incl. variables), `externalDocs` — `info` and `servers` are
  CLI-provided emit-time data (`--title`/`--version`/`--server`), not contract
  data; they are lost on import by design
- Non-JSON structured content (CBOR, YAML, JSON-Patch, …)

## Quality gate

Every import corpus in the suite must satisfy conformance check #4: importer
stability metrics *and* "the scaffolded C# compiles"
(`ImportMetricTests.Scaffolded_CSharp_Compiles`, `RealWorldImportTests`).
