# Import profile (`--from-openapi`)

> **Round-trip status (2026-10-26): 22/25 verified.** The hardened real-disk gate
> covers 5,198 operations and 6,435 normalized component identities: 5,843
> schemas, 78 request bodies, 345 parameters, 140 responses, and 29 security
> schemes, plus 12 header and 504 example components. All valid finding
> categories, unsupported markers, compilation, integrity, fixed-point,
> generated-carrier shape-class behavior, and recomputed source-to-first findings
> are empty. What the source documents themselves get wrong (273 sites: an empty
> parameter name, reserved headers, bodies on body-forbidden statuses, and paths
> that repeat a template under other parameter names) is bound by hash, pointer,
> diagnostic, and count in the profile. The three corpora still outside the gate,
> and exactly what keeps each one out, are listed under
> [Outside the gate](#outside-the-gate).
> The 25 source documents are committed under `openapi/`, pinned by SHA-256 in
> `corpus/openapi-manifest.json`, and `RoundTripCorpusGateTests` runs inside the
> plain `dotnet test` that every release tag's publish workflow runs first, so
> anyone with a checkout can reproduce the gate. Only the per-run reports
> (`TestResults/roundtrip/`) stay out of git.

`--from-openapi` is an **onboarding scaffold**, not a sync tool. Run it once to
bring an existing OpenAPI 3.x API into Rivet: it generates C# contracts + DTOs,
you review the diagnostics below, fix what matters, and from that point the C#
is the source of truth — the forward pipeline (`--openapi`) regenerates the spec
from your code. Re-running the import against a drifted spec is supported but is
a re-onboarding, not an incremental merge.

The importer contract is that nothing is dropped silently: anything the
generated C# representation cannot preserve must produce a stable `RIV`
diagnostic or a structured marker at the affected site. The verified corpus gate
proves that contract over its inventoried 21-corpus surface. Broader limitations
are listed below rather than being treated as permitted equivalences.

## Verified corpus inventory

`python3 tools/roundtrip_inventory.py` checks the artifacts against
`corpus/verified-profile.json`. It inventories all observed standard keywords,
component namespaces, and vendor-extension families and fails on an unknown
keyword/extension, artifact or profile drift, or changed reviewed disposition.

The source namespaces contain 5,643 `components.schemas` plus 6 Swagger
`definitions` (5,649 normalized schemas), 73 `components.requestBodies`, 345
`components.parameters`, 140 `components.responses`, 24
`components.securitySchemes`, and 2 Swagger `securityDefinitions` (26 normalized
security schemes). Together these are 6,233 normalized component identities.
GitHub and Discord add the first `components.headers` (12) and GitHub the first
`components.examples` (504); both survive by name, and a header `$ref` stays a
`$ref` wherever the source wrote one.
The full preserve/map/exclude table and value-based evidence are in
`corpus/verified-profile.json`.
Preserve/map families cover enum member metadata, concrete request/response
examples, OAuth permissions, read-only/deprecation mappings, lifecycle and SDK
visibility, Square request-version behavior, and Twilio client/resource metadata.
Catalog provenance, documentation navigation, vendor SDK naming, and
language-specific Java annotations are explicitly excluded rather than being
blanket-ignored. Box's 14, Kubernetes' 8, and Zoom's 4 newly observed private
extension families are explicit excludes; the profile does not claim
provider-specific Box behavior, Kubernetes discovery/merge/apply/union
semantics, or Zoom private example and enum-description behavior. The five
newest corpora brought 16 more families. The ones that change what a caller may
send or receive are preserved: Stripe's expandable fields and unvalidated enums,
GitHub's per-operation availability and preview metadata, GitHub's
slash-carrying path parameters, and Discord's "this anyOf is really exclusive".
Code samples, Speakeasy SDK naming, Stripe's SDK resource ids, GitHub's future
breaking-change notes and its `x-webhooks` map are excluded, each with its
evidence in the profile. That makes 97 reviewed families: 17 preserved, 7
mapped, 73 excluded.

All preserve rows survive as exact opaque JSON provenance at their owning
pointers through the fixed point; this does not mean Rivet interprets
provider-private semantics. Map rows are comparator-validated and excludes are
explicit. Swagger `collectionFormat` comparison is fixed and mutation-covered.
Named request-body components retain identity and content even when shared or
unused.

The carrier inventory contains 23,904 owner/shape classifications across 23,834
unique pointers. It covers named-property records, empty-schema `JsonElement`
carriers, propertyless open/closed objects, and 784 schema-valued dictionaries;
each observed carrier shape class is tied to an executable compiled-runtime
behavior test. This is shape-class proof, not an assertion that every pointer is
independently deserialized.

## Current represented behavior

The bullets below describe supported paths and known degradations. They are not
verified-profile full-gate or corpus-level cleanliness claims. A marker or diagnostic is
honest degradation, not full support.

- **Operations**: GET/HEAD/POST/PUT/PATCH/DELETE/OPTIONS; exact authored
  `operationId` presence/value and tags persist as generated provenance (as do
  the `x-rivet-contract`/`x-rivet-endpoint` extensions when present); summaries,
  descriptions, deprecation, and effective scoped servers are preserved.
  Documentation the typed model has no slot for (path-item `summary` and
  `description`, operation `externalDocs`, response and component `links`,
  media-type `encoding`, `info.summary` and `jsonSchemaDialect`) rides along as
  preserved fields: exact JSON, re-attached at the same pointer on emit. TRACE
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
- **Discriminated `oneOf`** with a `mapping` that names every variant: an
  abstract `[JsonPolymorphic]` base with one `[JsonDerivedType]` per tag, and each
  variant a record deriving from it, its tag property stripped because
  System.Text.Json writes the tag itself. A variant may be a plain object or an
  `allOf` of a `$ref`'d base and its own object (Cloudflare's rulesets); its parts
  are flattened in, and the union base stays its only parent. A tag the source
  wrote as something richer than a one-value enum (Jira's described
  `{ "type": "string" }`) travels on the variant as `DiscriminatorSchemaJson`, so
  the spec comes back as written. `properties`/`required` beside the `oneOf` are
  fine when they only restate the discriminator; anything more is a base-plus-union
  shape STJ can't express, and stays a `RIV3005`.
- **`allOf` inheritance under a discriminator** (OpenAPI 3.0's style, Bitbucket's
  `object`): a base with a `discriminator` and no `oneOf`, extended by components
  whose `allOf` names it. The tag is the `mapping` entry, or else the component's
  own name. Every member becomes a `[JsonPolymorphic]` record deriving from its
  parent's record and registering every concrete descendant, so a property typed
  `account` reads a `user`. A concrete member also registers itself and is never
  `abstract`, and leaves are polymorphic too, which is what makes a `User` you
  return directly still write `"type": "user"`. Children declare only what their
  parent's record doesn't already carry, a property redeclared with another type
  is written with `new`, and the discriminator property is stripped everywhere.
  An unknown tag fails to deserialize: a `type` Rivet never heard of is an error,
  not a bare base. A component whose own tag property can never hold its tag
  (Bitbucket's `pipeline_selector` redefines `type` as a selector kind) is named
  by no payload, so it stays a plain record outside the hierarchy. What C# can't
  hold (several `$ref`s in one `allOf`, a required member redeclared, two schemas
  claiming one tag) leaves the whole hierarchy a plain record with a `RIV3005`
  naming why.
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
  A named Example Object keeps its `summary` and `description`, and one that is
  *only* a summary (GitHub documents request variants that way; OpenAPI makes the
  value optional) is a whole example, not a gap.
- **Parameters**: path, query, header, and cookie parameters retain their exact
  name, location, requiredness, schema/reference identity, descriptions,
  deprecation, defaults, examples, constraints, and represented serialization
  metadata (including query `allowReserved` and `allowEmptyValue`) through explicit
  `.Parameter<T>(...)` provenance. Parameters and request bodies remain
  independent on the same operation, so query/cookie/body locations are not
  inferred from a merged input record or erased.
- **Schemas**: named objects → sealed records; `allOf` inheritance chains —
  including middle layers' own properties and top-level `required` tightening;
  `oneOf`/`anyOf` → `As*` wrapper union records; string/int enums; single-value
  branded primitives (`x-rivet-brand`); generics (`x-rivet-generic`);
  dictionaries via `additionalProperties`; `$ref` aliases (resolved to targets).
  OpenAPI 3.1's titled-const idiom (`oneOf` of `{"title": "ONE_HOUR", "const": 60}`)
  is an enum named by its titles, not a union of indistinguishable slots. An enum
  is a set, so a repeated member (Vercel lists `preview` twice) collapses to one;
  a `null` member is kept as a member, separately from nullability. An enum no
  C# enum can hold (booleans, numbers, arrays, `null`, a single value) keeps its
  values as generated metadata, and its C# type follows them: `{ enum: [true] }`
  is a `bool`, `{ enum: [[]] }` a `JsonElement`. A typed record's array items and map values keep their `$ref` to a
  named scalar, a nullable component is referenced bare (it already admits
  null), and `minProperties`/`maxProperties` survive on typed dictionaries.
  Framework scalar names are globally qualified when a generated component name
  would otherwise shadow their CLR type.
- **Nullability and requiredness — independent axes, both preserved.**
  3.0 (`nullable: true`) and 3.1 (`type` arrays, null branches) forms import on
  inline properties AND on components: a `$ref` to a component that is itself
  nullable resolves nullable at every use-site. A property that is required
  AND nullable scaffolds in the non-positional form with the C# `required`
  keyword (`public required T? X { get; init; }`) — must be present, may be
  null — and re-emits with both axes intact. Optionality is spelled
  `[RivetOptional]`, independent of nullability, so all four
  required/optional × nullable/non-nullable combinations round-trip. A null
  branch inside a 3+-variant `oneOf` union is also still dropped (the `As*`
  wrapper has no nullability slot; the `{"type": "null"}` degradation marker
  covers the 2-variant case).
- **Enum wire values**: every string member is pinned with
  `[JsonStringEnumMemberName]`; `Ready`, `COLLABORATOR`, `EastUs`, and already-
  camel-cased values survive runtime serialization and emission exactly. This
  attribute is a .NET 9+ API, so imported projects containing these generated
  string enums require .NET 9 or later.
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
- **Security**: all of it. An operation's requirements become ordinary builder
  calls you'd write by hand: each alternative starts with `.Secure(scheme, scopes...)`,
  every further scheme in that AND-group is an `.AndSecure(...)`, an empty
  requirement (`{}`) is `.OrAnonymous()`, and `security: []` is `.Anonymous()`. An
  operation with no `security` key writes none of them and inherits the document's.
  Document-level requirements and scheme definitions (API key in header/query/cookie,
  HTTP, every OAuth2 flow with its scopes, OpenID Connect, mutual TLS) land in
  `RivetSecurity.cs` as assembly attributes. The one exception is a requirement
  naming a scheme the document never defines: nobody can satisfy it, so that
  alternative is dropped with `RIV3030`, and an operation left with none is
  dropped rather than imported as open.

## What diagnoses

### Marker comments in scaffolded contracts — `// [rivet:unsupported …]`

| Marker | Meaning |
|---|---|
| `body $ref=… reason=unresolved-ref` | Request-body `$ref` could not be resolved; the input type was dropped. |
| `request-example …` / `response-example …` (`reason=unresolved-ref` / `missing-value` / `external-value`) | An example could not be resolved/carried. `externalValue` examples have no in-document JSON value and remain unsupported. |
| `header name=… status=… reason=undeclared-status` | Response header sits on a status the contract cannot declare (e.g. a non-lowest 2xx); the header was dropped. |
| `header name=… status=… reason=duplicate-name` | A second spelling of a header already declared on that status (`X-Rate` and `x-rate`); HTTP header names ignore case, so the first wins and this one was dropped. |
| `header name= status=… reason=empty-name` | Response header with an empty name; it can never be sent, so it was dropped. |
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
| `RIV3005` | `discriminator-dropped` | `discriminator` with no reversible polymorphic shape (a plain object with neither `oneOf` nor `allOf` descendants, an `allOf` hierarchy C#'s single inheritance can't hold, or a `oneOf` whose `mapping` is absent/unusable or whose siblings go beyond the discriminator) — dispatch semantics dropped. |
| `RIV3001`, `RIV3006`, `RIV3008` | `alias-unresolvable` | Cyclic / dangling `$ref` alias chains broken with placeholders. |
| `RIV3014` | `dictionary-key-dropped` | A `propertyNames` schema has no C# dictionary-key representation; keys degrade to `string`. |
| `RIV3015` | `named-scalar-algebra-unsupported` | A named scalar component uses `const`, schema composition, or heterogeneous leaf types; its fallback mapping is retained. |
| `RIV3003` | `operation-method-dropped` | TRACE operation dropped — the HTTP method has no contract representation. |
| `RIV3019` | `swagger-server-not-projected` | A Swagger 2 document has a `host` without `schemes`, or `schemes` without a `host`; no server URL is invented from half of one. |
| `RIV3020` | `empty-parameter-name-dropped` | Parameter name is empty, which OpenAPI forbids; the parameter is dropped. |
| `RIV3021` | `reserved-content-type-header-dropped` | `Content-Type` is declared as a header parameter instead of request-body content. |
| `RIV3022` | `reserved-authorization-header-dropped` | `Authorization` is declared as a header parameter instead of a security scheme. |
| `RIV3023` | `reserved-accept-header-dropped` | `Accept` is declared as a header parameter instead of response content. |
| `RIV3024` | `body-forbidden-status-content-dropped` | Response content or an example sits on a body-forbidden status (1xx/204/205/304); the content is dropped, the status kept. |
| `RIV3025` | `schemaless-parameter-dropped` | Parameter declares `content` instead of a `schema`, or neither; the parameter is dropped from the operation. |
| `RIV3027` | `callback-dropped` | Operation or component `callbacks` entry; contracts describe requests the API receives. |
| `RIV3028` | `path-item-outside-paths-dropped` | `webhooks` or `components.pathItems` entry; only path items under `paths` become endpoints. |
| `RIV3029` | `equivalent-path-template-merged` | A path repeats an earlier path's template under other parameter names, which OpenAPI forbids; its operations merge into the earlier path and adopt its parameter names. |
| `RIV3030` | `unsatisfiable-security-dropped` | A security alternative names a scheme the document never defines; the alternative is dropped, and an operation left with none is dropped too. |
| `RIV3031` | `integer-enum-value-unheld` | An integer enum lists a whole value its C# integer type can't hold; the values survive as metadata, but C# code using that type can't read or write those values. |

- **Paths**: two paths that differ only in their parameter names
  (`/deployments/{idOrUrl}` and `/deployments/{id}`) are one template, which
  OpenAPI forbids. When their methods don't clash, the later path's operations
  move to the earlier one and adopt its parameter names (`RIV3029`); requests
  don't change, because path parameter names never reach the wire. Twins that
  can't merge stop generation with `RIV2013` instead.
- **Discriminator mappings** into another file (`models/resize.yml`) cannot
  resolve in the one document Rivet imports. The entry is dropped with `RIV3005`
  before anything else reads it.

## Current unsupported or degraded surface

These currently have no complete generated C# representation and are not
preserved beyond the diagnostics above where applicable. This is an
implementation inventory, not an amendment to the verified profile:

- `callbacks` on operations and in components (`RIV3027`), and `webhooks` and
  `components.pathItems` (`RIV3028`)
- Parameters that carry `content` instead of a `schema` (`RIV3025`)
- Polymorphic `discriminator` dispatch with neither a usable `oneOf` mapping nor
  an `allOf` hierarchy C# can hold (imports as plain records/unions, loudly)
- General schema algebra beyond the generated provenance model, including
  malformed/cyclic aliases, arrays without `items`, and enum/const combinations
  that cannot become a C# type; these paths
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

The retained audit independently recompiles both generated-source passes and
recomputes source-to-first semantics for all 22 verified corpora, 5,198
operations, and 6,435 normalized component identities with no valid findings.
The source documents are committed and hash-pinned, and the gate runs in every
release's `dotnet test`; only the generated reports are gitignored.

## Outside the gate

Three corpora still fail the gate, and none of them is excused. Each failure is a
Rivet gap with a known shape, waiting on a design decision rather than a shrug:

- **Jira**: the discriminated variants Rivet synthesizes from the C# hierarchy
  lose their member descriptions and invent a `const`-like tag `enum` the source
  never wrote (about 450 findings). Its 144 bodies on 204 statuses and two paths
  that repeat a template are source defects the profile can bind.
- **DigitalOcean**: it has no reusable components; it reuses by `$ref` into other
  operations (`#/paths/~1v2~11-clicks/get/responses/401`), 2,797 times. The
  importer inlines those and the reference identity is gone, which is nearly all
  of its ~10,000 findings.
- **Cloudflare**: the largest of them, and the longest list: 9 unusable
  discriminators (`RIV3005`: three `oneOf`s with no mapping, two mappings that
  send several tags to one schema, two variants whose `allOf` base enum leaves out
  their own tag, one mapping entry that resolves nowhere, one variant two unions
  share), 7 scalar compositions (`RIV3015`), 5 identity-only fallbacks
  (`RIV3010`), and 25 unreviewed extension families.
