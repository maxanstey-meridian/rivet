# Diagnostics Reference

Every diagnostic Rivet writes to stderr carries a stable ID in a canonical,
machine-parseable format:

```
warning RIV1001: <message>
error RIV2002: <message>
```

IDs are stable across releases — grep, baseline, or suppress by ID, never by
message text. Most diagnostics are **warnings** and allow processing to continue.
Diagnostics marked **Error** are fatal and exit `1`; currently this applies to
`RIV1006`, `RIV1021`, `RIV1022`, `RIV1023`, `RIV1102`, `RIV1103`, `RIV1104`, `RIV2002`, `RIV2011`, and `RIV2012`. Coverage
warnings also exit `1` whenever `--check` is used, with or without `--output`; other
warnings do not change the exit code.

The ID ranges follow the pipeline stages:

| Range | Stage |
|---|---|
| `RIV1xxx` | Extraction (Roslyn walkers) |
| `RIV2xxx` | Emission (OpenAPI emitter, contract JSON reader) |
| `RIV3xxx` | Import (`--from-openapi` scaffold) |
| `RIV4xxx` | Coverage (`--check`) |

Within the extraction range, `RIV1024`-`RIV1099` are reserved for the
`rivet/php` sibling runtime. Native Rivet diagnostics must not use that block.

**Markers are not diagnostics.** The `// [rivet:unsupported …]` marker comments
that `--from-openapi` writes into scaffolded source are part of the generated
code, not stderr diagnostics — they have their own grammar and carry no RIV ID.
See the [Import Profile](/reference/import-profile) marker table.

The registry (`Rivet.Tool/Diagnostics.cs`) and this page are cross-checked in
both directions by the test suite (`DiagnosticsTests`) — every registered ID
must have a row here, and every row here must be a registered ID.

## RIV1xxx — extraction

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV1001` | Warning | Contract endpoint field is not `static readonly` — it may not be read correctly at generation time. | Declare the `Define` field as `public static readonly`. |
| `RIV1002` | Warning | Response example targets a status code the contract endpoint does not declare — the example is ignored. | Declare the status on the endpoint (`.Status(...)` / `.Error<...>(...)`) or fix the example's status code. |
| `RIV1003` | Warning | `[JsonPropertyName]` on a route-bound property is ignored for route interpolation — the contract param keeps the route name. | Align the route token and the property name, or accept that the route name wins for path params. |
| `RIV1004` | Warning | Response example targets a status code the controller endpoint does not declare — the example is ignored. | Declare the response status on the action (e.g. `[ProducesResponseType]`) or fix the example's status code. |
| `RIV1006` | Error | Unmapped typed result branch in `Results<...>`, or an action with no declared success response — generation fails instead of guessing or omitting the response. | Declare the branch with a fixed-status typed result or explicit `[ProducesResponseType]`; declare the success response with `[ProducesResponseType(typeof(T), 200)]`, a concrete payload return, or a fixed-status typed result. |
| `RIV1007` | Warning | Two walked types share a simple name — the later type is emitted under a disambiguated name (`Name2`, …). | Rename one of the colliding types to keep schema names stable. |
| `RIV1008` | Warning | A `[Range]` bound could not be parsed — the range constraint is skipped. | Use numeric `[Range]` bounds the walker can evaluate. |
| `RIV1009` | Warning | `TimeSpan` has no schema mapping — emitted as an untyped (empty) schema. | Expose the value as a supported type (e.g. ISO 8601 `string`, or seconds as a number). |
| `RIV1010` | Warning | `BigInteger` has no schema mapping — emitted as an untyped (empty) schema. | Expose the value as a supported type (e.g. `string`, or `long` when the range allows). |
| `RIV1013` | Warning | Dictionary key type has no contract representation (supported: `string`, enums, string-backed brands, string-serializable primitives like `Guid`/`DateTime`/`char`/numerics) — keys are emitted as unconstrained strings. | Use a supported key type, or accept the unconstrained string keys. |
| `RIV1014` | Warning | `[JsonDerivedType]` registration with a non-string (int or absent) discriminator tag — a string-discriminated `oneOf` cannot represent it; the base type falls back to plain property flattening. | Use string discriminator tags, or accept the flattened base schema. |
| `RIV1015` | Warning | `[JsonPolymorphic]` base type has no `[JsonDerivedType]` registrations — there is no variant set to emit; the type falls back to plain property flattening. | Register the derived types with `[JsonDerivedType]`, or remove `[JsonPolymorphic]`. |
| `RIV1016` | Warning | `[JsonPolymorphic]` `UnknownDerivedTypeHandling` has no spec representation — the emitted `oneOf` admits only the registered derived types. | Accept that unknown-type fallback behaviour is invisible to spec consumers. |
| `RIV1017` | Warning | `.WithResponseHeader()` targets a status code the contract endpoint does not declare — the header is ignored. | Declare the status on the endpoint (`.Returns(...)` / `.Status(...)`) or fix the header's status code. |
| `RIV1018` | Warning | `[RivetUnion]` wrapper has no variant properties — there is no union to emit; the type falls back to plain property flattening. | Give the wrapper one optional property per variant (the shape the importer generates), or remove the attribute. |
| `RIV1019` | Warning | Route token has no matching property on the endpoint's input type (after normalized matching: case-insensitive, `_`/`-` stripped) — emitted as an untyped string path param. | Add a matching property to the input type (any casing of the token's words), or accept the untyped string param. |
| `RIV1020` | Warning | The input type on a bodyless method (GET/DELETE/`.AcceptsBinary()`) is a dictionary, collection or scalar — it has no property surface to lower to query params, so the input is dropped (route tokens still emit as untyped path params). | Model the query string as a record with one property per param, or remove the input type. |
| `RIV1021` | Error | An authored contract declares the same response status more than once, including a `.Returns(...)` collision with the success status. Such a contract fails at runtime and generation therefore aborts. | Declare a `[RivetUnion]` type if the status genuinely returns multiple shapes and return it once; otherwise remove the duplicate (often one `.Returns(...)` is mistyped). |
| `RIV1022` | Error | `[RivetRequestBody]` names a body type that is not represented independently by the endpoint input type. | Use the importer-generated marker and composite input, or remove the invalid hand-authored override. |
| `RIV1023` | Error | Imported generated C# changed while raw schema provenance still targets its original typed shape. | Re-import the OpenAPI document, or remove/update the stale raw schema provenance before emitting. |
| `RIV1100` | Error | A parameter's binding source cannot be established from explicit declarations or the retained narrow conventions (exact route-token name match, `CancellationToken`, `IFormFile`) — the contract is refused rather than emitting an operation that silently omits the input. | Give the parameter an explicit binding (`[FromBody]`, `[FromQuery]`, `[FromRoute]`, `[FromHeader]`, `[FromForm]`, or `[FromServices]` for DI plumbing), or remove it from the action. |
| `RIV1101` | Warning | Reserved unsupported-`[JsonInclude]`-shape diagnostic. The mixed-accessor case (public accessor on one side, non-public on the other) is now determined as both-surface when `[JsonInclude]` is present — STJ serializes via the public accessor and deserializes via the included non-public accessor — and public/private `[JsonInclude]` fields are represented, so no currently supported shape triggers it; it is kept registered for future unsupported shapes. | Currently no supported shape triggers this ID. If a future serializer shape cannot be proven statically, the member will be reported here rather than guessed. |
| `RIV1102` | Error | A response example or response content is authored on a body-forbidden status (1xx, 204, 205, 304) — HTTP forbids a message body on those statuses, so the authored content could never reach the wire. Reported at frontend parsing and again at emission as defense in depth; generation fails before any output is written. | Move the example/content to a status that allows a body, or remove it; the ordinary bodyless 204 response needs no example. |
| `RIV1103` | Error | A `[RivetScalar]` type is not a non-generic class/struct/record with exactly one eligible non-static, non-indexer, non-implicit property named `Value`. | Give the type exactly one `Value` property (the Value type determines the scalar wire type), or remove `[RivetScalar]` if the type is an ordinary object. |
| `RIV1104` | Error | A multipart endpoint mixes `IFormFile` file parameters with an explicit body parameter — a `[FromBody]` JSON body or a `[FromForm]` DTO whose recursive form shape Rivet cannot faithfully lower without emulating MVC's recursive form binder — the contract is refused instead of silently dropping the declared input. | Split the form DTO into scalar fields (each scalar emits a form part beside the file), move a `[FromBody]` body to a separate JSON endpoint, or accept the file-only surface. |
| `RIV1106` | Warning | Two enum members produce the same wire value (via the declared converter's casing or duplicate `[JsonStringEnumMemberName]` pins) — the string union would not match the emitted wire values, so the enum is emitted as numeric instead. | Rename one of the colliding members (or fix its `[JsonStringEnumMemberName]`) so the wire values are distinct. |
| `RIV1107` | Error | An endpoint declares the same response status twice with different bodies — a `[ProducesResponseType]`/`.Returns(...)` declaration and a mapped `Results<>` branch disagree on the payload type, or one declares a body where the other declares none. One status carries exactly one shape; generation fails instead of silently letting the attribute win. | Align the attribute declaration and the `Results<>` branch (same payload type, or both bodyless), or drop the redundant declaration. |

`RIV1024`-`RIV1099` are documented by `rivet/php`; this repository reserves the
block but does not register those sibling-runtime diagnostics as native Rivet
diagnostics.

### Retired IDs

Retired IDs are never reused; they keep a tombstone here instead of a table row.

- ~~`RIV1005`~~ (`FromHeaderParameterExcluded`) — retired in P2 wave 5. `[FromHeader]`
  parameters used to be excluded from the contract with this warning; they now map to a
  first-class header parameter source (`in: header` in the emitted spec), so the
  diagnostic no longer exists.
- ~~`RIV1011`~~ (`UnsupportedChar`) — retired in P2 wave 6. `char` used to emit an
  untyped (empty) schema with this warning; it now maps to a length-1 `string` schema
  (`minLength`/`maxLength: 1` — the System.Text.Json wire shape) with
  `x-rivet-csharp-type: char`, so the diagnostic no longer exists.
- ~~`RIV1012`~~ (`UnsupportedObject`) — retired in P2 wave 6. `object` used to emit an
  untyped (empty) schema with this warning; the untyped schema *is* the honest spec for
  "any JSON value", so `object`/`object?` now map to it deliberately and silently, and
  the diagnostic no longer exists.
- ~~`RIV1105`~~ (`RivetEnumNamingPolicyWithoutStringConverter`) — retired in the
  converter-family wave (0.44.0). The casing convention now lives in the enum's
  `[JsonConverter]` declaration itself (`RivetCamelCaseEnumConverter<T>` family), so a
  policy cannot exist without a string wire and the dangling-marker case is unexpressible.

## RIV2xxx — emission

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV2001` | Warning | Synthesized tagged-union variant component collides with an existing schema — the existing schema wins. | Rename the colliding type or the union variant so component names stay distinct. |
| `RIV2002` | Error | Endpoint references a security scheme with no definition; generation fails rather than inventing security semantics. | Define the same scheme via `--security`, or fix the `.Secure("...")` name. |
| `RIV2003` | Warning | Two endpoints share an HTTP method + path and their declarations are equivalent — one representative is emitted. | Remove or re-route the redundant duplicate endpoint. |
| `RIV2004` | Warning | Multipart input type is absent from the contract's type definitions — the request schema is built inline from the endpoint's params. | Fix the upstream producer (rivet-ts/rivet-php lowerer) to include the input type definition. |
| `RIV2005` | Warning | `unknown` type (`JsonElement`/`JsonNode` or an unmapped C# type) in the OpenAPI schema — emitted as untyped. The message names the offending type/property or endpoint site. | Replace the named property/param type with a concrete supported type, or accept the untyped schema. |
| `RIV2006` | Warning | Unresolved generic type parameter in the OpenAPI schema — emitted as `object`. | Expose a closed generic instantiation; open type parameters cannot be emitted. |
| `RIV2007` | Warning | Generic template is absent from the contract's type definitions — a free-form object schema is emitted for the instantiation. | Fix the upstream producer to include the generic template definition. |
| `RIV2008` | Warning | Brand declared with conflicting underlying types — the first declaration wins. | Align every declaration of the brand on one underlying type. |
| `RIV2009` | Warning | Header parameter named `Accept`, `Content-Type` or `Authorization` — OpenAPI forbids these as header parameters; the parameter is omitted from the spec. | Describe content negotiation via media types and auth via security schemes; use a custom header name for anything else. |
| `RIV2010` | Warning | External contract IR declares the same response status more than once — the duplicate is dropped and the first declaration, including its metadata, is kept. | Fix the producer to emit one response per status; use a union schema when one status genuinely has multiple payload shapes. |
| `RIV2011` | Error | A programmatic security configuration defines the primary scheme name again in its additional definitions. | Keep each scheme name unique; the CLI already rejects duplicate `--security` names. |
| `RIV2012` | Error | Two incompatible endpoints declare the same normalized HTTP method + route (e.g. contradictory response shapes from different extraction frontends) — generation fails naming both sources instead of writing a lossy spec. | Resolve the contradiction at the source; equivalent duplicate declarations collapse silently. |

## RIV3xxx — import

Import warnings are also surfaced programmatically on
`ImportResult.Warnings`, where each entry carries its ID as a `RIV3001: `
prefix. The test-suite ratchet categories these map to are listed in the
[Import Profile](/reference/import-profile#warnings-stderr-importresult-warnings).

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV3001` | Warning | Alias schema is part of a `$ref` cycle — replaced with an empty schema; consumers resolve to an untyped object. | Break the cycle in the source spec, or type the affected members after scaffolding. |
| `RIV3002` | Warning | Document declares multiple security schemes — only the first is imported; alternatives and scopes are not represented. | Review the scaffold's `.Secure(...)` calls; multi-scheme semantics are out of scope. |
| `RIV3003` | Warning | TRACE operation dropped — the HTTP method has no contract representation. | No action; TRACE is out of scope for the contract model. |
| `RIV3004` | Retired | Reserved diagnostic ID. Mixed named objects now retain typed properties plus extension data. | No action. |
| `RIV3005` | Warning | `discriminator` with no reversible polymorphic shape (plain object without `oneOf`, or `oneOf` whose `mapping` is absent/unusable) — imported without dispatch semantics. | Model the polymorphism as a `oneOf` with a complete `discriminator.mapping` whose variants carry the tag property, or accept the non-polymorphic import. |
| `RIV3006` | Warning | Alias schema references a missing schema — consumers fall back to `JsonElement`. | Fix the dangling `$ref` in the source spec, or type the member after scaffolding. |
| `RIV3007` | Warning | Alias schema is part of a `$ref` cycle — consumers fall back to `JsonElement`. | Break the cycle in the source spec, or type the member after scaffolding. |
| `RIV3008` | Warning | Reference to an unresolvable alias schema (cycle or missing target) — using `JsonElement`. | Fix the alias it references (see `RIV3006`/`RIV3007`). |
| `RIV3009` | Warning | Schema could not be resolved to a C# type — mapped to `JsonElement`. | Type the member by hand in the scaffolded C#. |
| `RIV3010` | Warning | Unhandled JSON Schema `type` — mapped to `JsonElement`. | Type the member by hand in the scaffolded C#. |
| `RIV3011` | Warning | Array schema without `items` — mapped to `List<JsonElement>`. | Add `items` to the source spec, or type the list element after scaffolding. |
| `RIV3012` | Warning | Enum constraint that cannot become a C# enum (single-value, mixed/float, out-of-int32-range) — degraded to a primitive. | Accept the primitive, or hand-model the constraint in the scaffolded C#. |
| `RIV3013` | Retired | Reserved diagnostic ID. Mixed inline objects now retain typed properties plus extension data. | No action. |
| `RIV3014` | Warning | Dictionary `propertyNames` schema has no C# dictionary-key representation — imported with `string` keys. | Reference a string enum / string-backed brand or use a string key schema in the source spec, or type the dictionary key by hand after scaffolding. |
| `RIV3015` | Warning | Named scalar component uses const, composition, heterogeneous leaves, or a non-flat enum outside bounded scalar preservation. | Replace it with one primitive leaf (optionally nullable) or a flat string/Int32 enum; the existing fallback mapping is retained. |
| `RIV3020` | Warning | Parameter has an empty name, so the invalid parameter is dropped while the operation and its other parameters are preserved. | Give the source parameter a non-empty OpenAPI name. |
| `RIV3021` | Warning | Imported operation declares `Content-Type` as a header parameter even though OpenAPI represents request media types through `requestBody.content`. | Remove the reserved header parameter and retain the corresponding request-body media type. |
| `RIV3022` | Warning | Imported operation declares `Authorization` as a header parameter even though OpenAPI represents authentication through security schemes. | Remove the reserved header parameter and retain the corresponding operation or document security requirement. |
| `RIV3023` | Warning | Imported operation declares `Accept` as a header parameter even though OpenAPI represents response media types through response content. | Remove the reserved header parameter and retain the corresponding response media type. |
| `RIV3024` | Warning | Imported operation authors response content or an example on a body-forbidden status (1xx/204/205/304); the content is dropped while the status, description and headers are preserved. | Remove the body from the forbidden status in the source specification — HTTP forbids a message body on 1xx/204/205/304. |

## RIV4xxx — coverage (`--check`)

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV4001` | Warning | Contract endpoint has no matching implementation. | Implement the endpoint, or remove it from the contract. |
| `RIV4002` | Warning | Implementation HTTP method differs from the contract's. | Align the implementation's HTTP method with the contract. |
| `RIV4003` | Warning | Implementation route differs from the contract's. | Align the implementation's route with the contract. |
| `RIV4004` | Warning | A contract endpoint is bound in a route handler but no terminal result from that binding is returned. | Return a `Success`, `Error`, or `File` terminal through the correct host adapter, or remove the unused `Bind`. |
