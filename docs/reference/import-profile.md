# Import profile (`--from-openapi`)

> **Round-trip status (2026-07-13): 13/25 verified locally.** The real-disk gate
> covers 1,599 operations and 2,366 normalized component identities: 2,081
> schemas, 69 request bodies, 107 parameters, 95 responses, and 14 security
> schemes. All valid finding categories, unsupported markers, compilation,
> integrity, and fixed-point findings are empty. Eleven exact invalid-source
> constructs are hash- and pointer-bound in the verified profile. Corpus
> artifacts and reports remain local/gitignored, so this is not reproducible CI
> proof.

`--from-openapi` is an **onboarding scaffold**, not a sync tool. Run it once to
bring an existing OpenAPI 3.x API into Rivet: it generates C# contracts + DTOs,
you review the diagnostics below, fix what matters, and from that point the C#
is the source of truth — the forward pipeline (`--openapi`) regenerates the spec
from your code. Re-running the import against a drifted spec is supported but is
a re-onboarding, not an incremental merge.

The importer contract is that nothing is dropped silently: anything the
generated C# representation cannot preserve must produce a stable `RIV`
diagnostic or a structured marker at the affected site. The verified corpus gate
proves that contract over its inventoried 13-corpus surface. Broader limitations
are listed below rather than being treated as permitted equivalences.

## Verified corpus inventory

`python3 tools/roundtrip-inventory.py` checks the artifacts against
`corpus/verified-profile.json`. It inventories all observed standard keywords,
component namespaces, and vendor-extension families and fails on an unknown
keyword/extension, artifact or profile drift, or changed reviewed disposition.

The source namespaces contain 2,075 `components.schemas` plus 6 Swagger
`definitions` (2,081 normalized schemas), 69 `components.requestBodies`, 107
`components.parameters`, 95 `components.responses`, 12
`components.securitySchemes`, and 2 Swagger `securityDefinitions` (14 normalized
security schemes). Together these are 2,366 normalized component identities.
The full preserve/map/exclude table and value-based evidence are in
`corpus/verified-profile.json`.
Preserve/map families cover enum member metadata, concrete request/response
examples, OAuth permissions, read-only/deprecation mappings, lifecycle and SDK
visibility, Square request-version behavior, and Twilio client/resource metadata.
Catalog provenance, documentation navigation, vendor SDK naming, and
language-specific Java annotations are explicitly excluded rather than being
blanket-ignored.

All preserve rows survive as exact opaque JSON provenance at their owning
pointers through the fixed point; this does not mean Rivet interprets
provider-private semantics. Map rows are comparator-validated and excludes are
explicit. Swagger `collectionFormat` comparison is fixed and mutation-covered.
Named request-body components retain identity and content even when shared or
unused.

## Current represented behavior

The bullets below describe supported paths and known degradations. They are not
verified-profile full-gate or corpus-level cleanliness claims. A marker or diagnostic is
honest degradation, not full support.

- **Operations**: GET/HEAD/POST/PUT/PATCH/DELETE/OPTIONS; exact authored
  `operationId` presence/value and tags persist as generated provenance (as do
  the `x-rivet-contract`/`x-rivet-endpoint` extensions when present); summaries,
  descriptions, deprecation, and effective scoped servers are preserved. TRACE
  operations have no contract representation and are dropped with a named
  warning (`RIV3003` / `operation-method-dropped`).
- **Bodies**: `application/json` (typed records), `application/x-www-form-urlencoded`
  (form-encoded inputs), `multipart/form-data` (incl. `IFormFile` /
  `List<IFormFile>`), binary content types (file endpoints / `ProducesFile`),
  `text/*` and `*/*` fallbacks. A `text/*` body or success response keeps its
  media type via `.AcceptsContentType(...)` / `.ProducesContentType(...)` and
  the complete media-type map persists through explicit request-content
  provenance. Request-body presence and requiredness are represented independently
  with `.RequestBody()` / `.RequestBodyRequired(...)`; an optional body therefore
  stays optional even when the operation also declares parameters.
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
  VALUES (the github anti-pattern) retain the authored reference and carry the
  reachable component-example closure through the generated contract, so a
  round-trip preserves their shape without dangling them.
  Unresolvable refs degrade loudly (`unresolvable-embedded-example-ref`).
- **Parameters**: path, query, header, and cookie parameters retain their exact
  name, location, requiredness, schema/reference identity, descriptions,
  deprecation, defaults, examples, constraints, and represented serialization
  metadata through explicit `.Parameter<T>(...)` provenance. Parameters and
  request bodies remain independent on the same operation, so query/cookie/body
  locations are not inferred from a merged input record or erased.
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
- **Responses**: the exact response-key set survives, including concrete codes,
  informational responses, `2XX`/`4XX`/`5XX` ranges, and `default`; keys are not
  narrowed to representative concrete statuses. Every response retains its
  description, complete media-type map, schema/reference identity, and examples.
  JSON `null` examples import as `null` rather than the Microsoft.OpenApi
  sentinel string.
- **Response headers**: typed `.WithResponseHeader<T>(...)` /
  `.WithResponseHeaderKey<T>(...)` provenance preserves schema type/format,
  description, requiredness, examples, deprecation, serialization metadata, and
  header content. Headers are not restricted to `string`.
- **Validation metadata on schema properties**: min/max length, pattern,
  ranges, item counts → DataAnnotations / Rivet constraint attributes;
  defaults, formats, descriptions, examples, readOnly/writeOnly. Records
  whose properties carry a `ValidationAttribute` are scaffolded as
  non-positional `required`/`init` records — the MVC-safe placement (see the
  [positional-record gotcha](/guides/runtime-validation#the-positional-record-gotcha));
  unconstrained records stay positional.
- **Document provenance**: info/contact/licence, tags and external docs, root and
  scoped servers (including variables), Swagger 2 host/basePath/schemes,
  component examples, operation identity, and request-body descriptions persist
  in generated assembly/field attributes. Explicit current CLI title/version/
  server values override imported provenance.
- **Security**: global and operation requirement alternatives, AND combinations,
  scopes, empty requirements, and scheme definitions persist in generated
  provenance. Supported definitions include API key (header/query/cookie), HTTP,
  OAuth2 flows, OpenID Connect, and mutual TLS.

## What diagnoses

### Marker comments in scaffolded contracts — `// [rivet:unsupported …]`

| Marker | Meaning |
|---|---|
| `body $ref=… reason=unresolved-ref` | Request-body `$ref` could not be resolved; the input type was dropped. |
| `request-example …` / `response-example …` (`reason=unresolved-ref` / `missing-value` / `external-value`) | An example could not be resolved/carried. `externalValue` examples have no in-document JSON value and remain unsupported. |
| `header name=… status=… reason=undeclared-status` | Response header sits on a status the contract cannot declare (e.g. a non-lowest 2xx); the header was dropped. |
| `param name=… in=… reason=reserved-member-renamed` | The parameter name maps to reserved C# record machinery and cannot bind under the generated member name; the rename is explicit. |

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
| `RIV3014` | `dictionary-key-dropped` | A `propertyNames` schema has no C# dictionary-key representation; keys degrade to `string`. |
| `RIV3015` | `named-scalar-algebra-unsupported` | A named scalar component uses const/composition/heterogeneous leaves outside bounded scalar preservation; its fallback mapping is retained. |
| `RIV3003` | `operation-method-dropped` | TRACE operation dropped — the HTTP method has no contract representation. |
| `RIV3020` | `empty-parameter-name-dropped` | Parameter name is empty, which OpenAPI forbids; the parameter is dropped. |
| `RIV3021` | `reserved-content-type-header-dropped` | `Content-Type` is declared as a header parameter instead of request-body content. |
| `RIV3022` | `reserved-authorization-header-dropped` | `Authorization` is declared as a header parameter instead of a security scheme. |
| `RIV3023` | `reserved-accept-header-dropped` | `Accept` is declared as a header parameter instead of response content. |

## Current unsupported or degraded surface

These currently have no complete generated C# representation and are not
preserved beyond the diagnostics above where applicable. This is an
implementation inventory, not an amendment to the verified profile:

- `callbacks`, `webhooks`, `links`
- Parameter `content`, `allowReserved`, and `allowEmptyValue`; `style` and
  `explode` are represented by the explicit parameter metadata channel
- Polymorphic `discriminator` dispatch *without* a usable `oneOf` mapping
  (imports as plain records/unions, loudly); usable mappings reverse to
  `[JsonPolymorphic]`/`[JsonDerivedType]` hierarchies
- General schema algebra beyond the generated provenance model, including mixed
  `properties` + `additionalProperties`, malformed/cyclic aliases, arrays without
  `items`, and enum/const combinations that cannot become a C# type; these paths
  diagnose and degrade rather than silently claim equivalence
- Operation-level `externalValue` examples, which have no in-document value to
  attach to the generated endpoint

## Quality gate

The implemented gate in `RoundTripCorpusGateTests` performs for each verified
corpus: manifest hash verification; production-CLI import with no unclassified
warnings/errors; zero unsupported markers in generated C#; loose-file
compilation; production-CLI re-emission with zero warnings/errors; OpenAPI 3.1
and reference/security integrity validation; recursive semantic comparison; and
a second import/re-emission proving the declared fixed point. Artifact,
diagnostic/marker, compilation, document, operation, schema, integrity, and
fixed-point findings are reported independently, and any non-zero category
fails. Exact invalid-source classifications are pinned by corpus hash, pointer,
diagnostic, and cardinality; they are not tolerated valid-contract findings.

The retained audit covers all 13 verified corpora, 1,599 operations, and 2,366
normalized component identities with no valid findings. The source documents and
generated reports are gitignored local artifacts, so this remains local
verification rather than reproducible CI proof.
