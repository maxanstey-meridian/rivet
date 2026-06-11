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
  (`operation-method-dropped`, see below).
- **Bodies**: `application/json` (typed records), `application/x-www-form-urlencoded`
  (form-encoded inputs), `multipart/form-data` (incl. `IFormFile` /
  `List<IFormFile>`), binary content types (file endpoints / `ProducesFile`),
  `text/*` and `*/*` fallbacks.
- **Parameters**: path and query parameters → synthesized input records
  (header/cookie parameters are folded in too, but see the markers below).
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
  branded primitives (`x-rivet-brand`); generics (`x-rivet-generic`); nullable
  in both 3.0 (`nullable: true`) and 3.1 (`type` arrays, null branches) forms;
  dictionaries via `additionalProperties`; `$ref` aliases (resolved to targets).
- **Responses**: lowest concrete 2xx wins (a `2XX` wildcard maps to 200 when no
  concrete 2xx exists); typed error responses; `default` → 500; `4XX`/`5XX` →
  400/500; named and `$ref` component examples.
- **Validation metadata on schema properties**: min/max length, pattern,
  ranges, item counts → DataAnnotations / Rivet constraint attributes;
  defaults, formats, descriptions, examples, readOnly/writeOnly.
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
| `param name=… in=header\|cookie reason=location-erased-to-query` | Header/cookie parameter folded into the input record; it will re-emit as a query parameter. |
| `param-metadata params=… reason=metadata-dropped` | Parameter descriptions / deprecation / validation constraints did not survive into the synthesized input record. |
| `param name=… in=… reason=dropped-unmergeable-body body-type=…` | Operation has both parameters and a request body whose type is not a plain record — the parameter could not be merged and was dropped. |
| `param name=… in=… reason=body-property-shadowed-by-param body-type=…` | A body property shares a parameter's name but not its type; the parameter won, the body property was dropped. |
| `param name=… in=query reason=location-erased-to-body` | Query parameter merged into a body-carrying operation's input record; it will re-emit inside the JSON body. |

### Warnings (stderr / `ImportResult.Warnings`)

Each warning belongs to a named category, ratcheted by the test suite
(`ImportMetricTests.CategorizeWarning`) — new categories are added consciously,
never absorbed:

| Category | Trigger |
|---|---|
| `unresolved-schema` | Schema could not be resolved to a C# type → `JsonElement`. |
| `unsupported-schema-type` | Unhandled JSON Schema `type` → `JsonElement`. |
| `array-missing-items` | Array schema without `items` → `List<JsonElement>`. |
| `enum-constraint-dropped` | Enum that can't be a C# enum (single-value, mixed, out-of-range) degrades to a primitive. |
| `discriminator-dropped` | `discriminator` on a plain object (no `oneOf`) — dispatch semantics dropped. |
| `alias-unresolvable` | Cyclic / dangling `$ref` alias chains broken with placeholders. |
| `properties-dropped` | Schema declares both `properties` and `additionalProperties`; the dictionary side won (inline objects). |
| `additional-properties-dropped` | Same conflict on a named schema; the record side won. |
| `security-schemes-dropped` | Document-level `security` declares multiple schemes; only the first imported. |
| `operation-method-dropped` | HEAD/OPTIONS/TRACE operation dropped — the HTTP method has no contract representation. |

## Out of scope

These have no C# contract representation and are not imported (beyond the
diagnostics above where applicable):

- `callbacks`, `webhooks`, `links`, response headers
- Parameter serialization (`style`, `explode`, `allowReserved`), XML mappings
- Polymorphic `discriminator` dispatch (schemas import as plain records/unions)
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
