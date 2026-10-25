# Diagnostics Reference

Every diagnostic Rivet writes to stderr carries a stable ID in a canonical,
machine-parseable format:

```
warning RIV1019: <message>
error RIV2002: <message>
```

IDs are stable across releases — grep, baseline, or suppress by ID, never by
message text. Most diagnostics are **warnings** and allow processing to continue.
Diagnostics marked **Error** are fatal and exit `1`; currently this applies to
`RIV1001`, `RIV1002`, `RIV1004`, `RIV1006`, `RIV1007`, `RIV1010`, `RIV1014`, `RIV1017`, `RIV1018`, `RIV1020`, `RIV1021`, `RIV1022`, `RIV1023`, `RIV1100`, `RIV1102`, `RIV1103`, `RIV1104`, `RIV1106`, `RIV1107`, `RIV1108`, `RIV1109`, `RIV1110`, `RIV1111`, `RIV1112`, `RIV1113`, `RIV1114`, `RIV2001`, `RIV2002`, `RIV2005`, `RIV2006`, `RIV2007`, `RIV2008`, `RIV2010`, `RIV2011`, `RIV2012`, `RIV2013`, `RIV4005`, and `RIV4006`. Coverage
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
| `RIV1001` | Error | Contract endpoint field is not `static readonly`. Rivet reads the spec from the field's initializer, so a field that can be reassigned, or exists once per instance, could serve a different definition at runtime than the one in the spec. | Declare the `Define` field as `public static readonly`. |
| `RIV1002` | Error | Response example targets a status code the contract endpoint does not declare — the spec has no response to put it on, so generation fails rather than quietly losing it. | Declare the status on the endpoint (`.Returns(...)` / `.Status(...)`) or fix the example's status code. |
| `RIV1003` | Warning | `[JsonPropertyName]` on a route-bound property is ignored for route interpolation — the contract param keeps the route name. | Align the route token and the property name, or accept that the route name wins for path params. |
| `RIV1004` | Error | Response example targets a status code the controller endpoint does not declare — the spec has no response to put it on, so generation fails rather than quietly losing it. | Declare the response status on the action (e.g. `[ProducesResponseType]`) or fix the example's status code. |
| `RIV1006` | Error | Unmapped typed result branch in `Results<...>`, or an action with no declared success response — generation fails instead of guessing or omitting the response. | Declare the branch with a fixed-status typed result or explicit `[ProducesResponseType]`; declare the success response with `[ProducesResponseType(typeof(T), 200)]`, a concrete payload return, or a fixed-status typed result. |
| `RIV1007` | Error | Two walked types share a simple name: different namespaces, nesting, or generic arity (`Result` and `Result<T>`). Schema names are global and built from the simple name alone, so generation fails and names both types. | Rename one of the two types. |
| `RIV1008` | Warning | A `[Range]` bound could not be parsed — the range constraint is skipped. | Use numeric `[Range]` bounds the walker can evaluate. |
| `RIV1010` | Error | `BigInteger` has no System.Text.Json converter: it serializes as an object of its `IsZero`/`Sign`/… properties and can't be read back, so no schema describes it honestly. | Expose the value as a `string` of digits, or as `long` or `decimal` when the range allows. |
| `RIV1013` | Warning | Dictionary key type has no contract representation (supported: `string`, enums, string-backed brands, string-serializable primitives like `Guid`/`DateTime`/`char`/numerics) — keys are emitted as unconstrained strings. | Use a supported key type, or accept the unconstrained string keys. |
| `RIV1014` | Error | `[JsonDerivedType]` registration with an int or missing discriminator tag. A string-discriminated `oneOf` can't represent an int tag, and a tagless registration writes no discriminator at all. | Give every registration a string tag: `[JsonDerivedType(typeof(Circle), "circle")]`. |
| `RIV1015` | Warning | `[JsonPolymorphic]` base type has no `[JsonDerivedType]` registrations — there is no variant set to emit; the type falls back to plain property flattening. | Register the derived types with `[JsonDerivedType]`, or remove `[JsonPolymorphic]`. |
| `RIV1016` | Warning | `[JsonPolymorphic]` `UnknownDerivedTypeHandling` has no spec representation — the emitted `oneOf` admits only the registered derived types. | Accept that unknown-type fallback behaviour is invisible to spec consumers. |
| `RIV1017` | Error | `.WithResponseHeader()` targets a status code the contract endpoint does not declare — the spec has no response to put it on, so generation fails rather than leaving out a header the handler sends. | Declare the status on the endpoint (`.Returns(...)` / `.Status(...)`) or fix the header's status code. |
| `RIV1018` | Error | `[RivetUnion]` wrapper has no variant properties, so there is no union to emit. | Give the wrapper one optional property per variant (the shape the importer generates), or remove the attribute. |
| `RIV1019` | Warning | Route token has no matching property on the endpoint's input type (after normalized matching: case-insensitive, `_`/`-` stripped) — emitted as an untyped string path param. | Add a matching property to the input type (any casing of the token's words), or accept the untyped string param. |
| `RIV1020` | Error | The input type on a request with no JSON body (GET, DELETE, `.AcceptsBinary()`) is a dictionary, collection or scalar. Such an input must lower to route and query params, and this one has no properties to lower, so generation fails instead of emitting an operation without it. | Model the input as a record with one property per route or query param, or send the values as a JSON body with `Define.Post/Put/Patch`. |
| `RIV1021` | Error | An authored contract declares the same response status more than once, including a `.Returns(...)` collision with the success status. Such a contract fails at runtime and generation therefore aborts. | Declare a `[RivetUnion]` type if the status genuinely returns multiple shapes and return it once; otherwise remove the duplicate (often one `.Returns(...)` is mistyped). |
| `RIV1022` | Error | `[RivetRequestBody]` names a body type that is not represented independently by the endpoint input type. | Use the importer-generated marker and composite input, or remove the invalid hand-authored override. |
| `RIV1023` | Error | Imported generated C# changed while raw schema provenance still targets its original typed shape. | Re-import the OpenAPI document, or remove/update the stale raw schema provenance before emitting. |
| `RIV1100` | Error | A parameter's binding source cannot be established from explicit declarations or the retained narrow conventions (exact route-token name match, `CancellationToken`, `IFormFile`) — the contract is refused rather than emitting an operation that silently omits the input. | Give the parameter an explicit binding (`[FromBody]`, `[FromQuery]`, `[FromRoute]`, `[FromHeader]`, `[FromForm]`, or `[FromServices]` for DI plumbing), or remove it from the action. |
| `RIV1101` | Warning | Reserved unsupported-`[JsonInclude]`-shape diagnostic. The mixed-accessor case (public accessor on one side, non-public on the other) is now determined as both-surface when `[JsonInclude]` is present — STJ serializes via the public accessor and deserializes via the included non-public accessor — and public/private `[JsonInclude]` fields are represented, so no currently supported shape triggers it; it is kept registered for future unsupported shapes. | Currently no supported shape triggers this ID. If a future serializer shape cannot be proven statically, the member will be reported here rather than guessed. |
| `RIV1102` | Error | A response example or response content is authored on a body-forbidden status (1xx, 204, 205, 304) — HTTP forbids a message body on those statuses, so the authored content could never reach the wire. Reported at frontend parsing and again at emission as defense in depth; generation fails before any output is written. | Move the example/content to a status that allows a body, or remove it; the ordinary bodyless 204 response needs no example. |
| `RIV1103` | Error | A `[RivetScalar]` type is not a non-generic class/struct/record with one public readable `Value` property and a matching public constructor, without inheritance, competing converters or property-level JSON settings. | Give the type exactly one `Value` property (the Value type determines the scalar wire type), or remove `[RivetScalar]` if the type is an ordinary object. |
| `RIV1104` | Error | An endpoint mixes separate form fields or files with an explicit body parameter — a `[FromBody]` JSON body or a `[FromForm]` DTO whose recursive form shape Rivet cannot faithfully lower without emulating MVC's recursive form binder — the contract is refused instead of silently dropping the declared input. | Split the form DTO into scalar fields (each scalar emits a form part beside the file), move a `[FromBody]` body to a separate JSON endpoint, or accept the file-only surface. |
| `RIV1106` | Error | Two enum members produce the same wire value (via the declared converter's casing or duplicate `[JsonStringEnumMemberName]` pins) — generation fails instead of emitting an incorrect numeric schema. | Rename one of the colliding members (or fix its `[JsonStringEnumMemberName]`) so the wire values are distinct. |
| `RIV1108` | Error | Unsupported enum converter, wrong generic enum target, or string flags/aliases. | Use a built-in or Rivet enum converter with distinct values; use numeric serialization for flags. |
| `RIV1107` | Error | An endpoint declares the same response status twice with different bodies — a `[ProducesResponseType]`/`.Returns(...)` declaration and a mapped `Results<>` branch disagree on the payload type, or one declares a body where the other declares none. One status carries exactly one shape; generation fails instead of silently letting the attribute win. | Align the attribute declaration and the `Results<>` branch (same payload type, or both bodyless), or drop the redundant declaration. |
| `RIV1109` | Error | A contract endpoint declares the same response header more than once for one status. Header names are case-insensitive, so `.WithResponseHeader("ETag").WithResponseHeader("etag")` is a duplicate; generation fails instead of emitting both spellings. | Keep one `.WithResponseHeader(...)` per header name and status. |
| `RIV1110` | Error | A `[RivetEndpoint]` or `[RivetClient]` action binds a string-wire enum (a `Rivet*EnumConverter` policy or a `[JsonStringEnumMemberName]` rename) from route, query, header or form, directly or as a member, at any depth, of a complex parameter the host binds member by member (the same cases as `RIV4005`), and the host would refuse an advertised wire value. An MVC action's enum model binder matches member names ignoring case, so camel-case and lowercase policies bind there, while snake, kebab and letter-changing renames do not. A static `[RivetEndpoint]` is a minimal-API handler, which parses with `Enum.TryParse` and needs the exact member name. Either way the JSON converter is ignored, so a client following the spec gets a 400. JSON bodies are unaffected. | Use a plain enum (integers on the wire), the built-in `JsonStringEnumConverter<T>` without renames, or bind a `string` and parse it in the handler. On MVC, an explicit `[ModelBinder]` on the parameter or enum exempts it. |
| `RIV1111` | Error | A `[JsonConverter]` Rivet can't model sits on a walked type, property or field: anything but a supported converter on the enum type, `[RivetScalar]` or `[RivetUnion]`. The wire is whatever the converter writes, so the spec would describe the CLR shape instead. | On a primitive-typed property, pin the wire with `[RivetSchemaType("string")]` (or whichever JSON type the converter writes). Otherwise make the value a `[RivetScalar]` wrapper, or put a supported converter on the enum type itself. |
| `RIV1112` | Error | `[JsonNumberHandling]` with `WriteAsString` or `AllowNamedFloatingPointLiterals` reaches a number, or a collection of numbers, on a walked type, from the member or from the type being serialized. Strings go on the wire where the spec says number. `AllowReadingFromString` alone is accepted: numbers are still written as numbers, it only widens what the host reads. | Remove it, or pin a primitive-typed property with `[RivetSchemaType("string")]`. |
| `RIV1113` | Error | A contract endpoint field can't be read statically: it has no initializer, its initializer isn't a `Define.*` builder chain (a helper call or another field, say), the chain calls something that isn't a Rivet builder method, or its route, a `.Secure()`/`.AndSecure()` scheme name or one of their scopes isn't a compile-time constant. Rivet used to skip such a field (or, for a non-constant scheme, quietly drop the security), so the operation vanished from the spec without a word. | Initialise the field with `Define.*` followed by Rivet builder calls only, with literal or `const` routes, scheme names and scopes. |
| `RIV1114` | Error | A contract endpoint's security calls contradict each other: `.Anonymous()` (no security requirement at all) sits alongside `.Secure()` or `.OrAnonymous()`, or `.AndSecure()` has no `.Secure()` alternative to join (it comes first, or straight after `.OrAnonymous()`). Rivet refuses rather than guessing which call you meant. | Use `.Anonymous()` alone for an open endpoint, `.Secure(...)` plus `.OrAnonymous()` for optional credentials, and start every AND-group with `.Secure(...)`. |

`RIV1024`-`RIV1099` are documented by `rivet/php`; this repository reserves the
block but does not register those sibling-runtime diagnostics as native Rivet
diagnostics.

## RIV2xxx — emission

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV2001` | Error | A tagged-union variant whose producer pinned its component id (`metadata.componentId`) lands on an id another schema already uses with a different shape. Unpinned variants never get here: a taken `{Base}_{Tag}` name picks up a numeric suffix, so each variant always `$ref`s its own schema. | Give the variant's `componentId` a name of its own, or make the variant a `ref` to the existing type. |
| `RIV2002` | Error | A document or endpoint security requirement names a scheme with no definition; generation fails rather than inventing security semantics. | Define the scheme via `--security` or `[assembly: RivetSecurityScheme(...)]`, or fix the `.Secure("...")`/`.AndSecure("...")` name. |
| `RIV2003` | Warning | Two endpoints share an HTTP method + path and their declarations are equivalent — one representative is emitted. | Remove or re-route the redundant duplicate endpoint. |
| `RIV2004` | Warning | Multipart input type is absent from the contract's type definitions — the request schema is built inline from the endpoint's params. | Fix the upstream producer (rivet-ts/rivet-php lowerer) to include the input type definition. |
| `RIV2005` | Error | A type with no schema mapping (an `unknown` primitive whose `csharpType` isn't `object`, `JsonElement`, `JsonNode`, `JsonObject` or `JsonArray`), or a `$ref` to a schema component nothing defines. The message names the type and its site, or the dangling component and where it's referenced. "Any JSON value" — TypeScript's `unknown`, `object`, `JsonElement`, the `JsonNode` family — is not this: it maps to the empty schema without a word. | Replace the named type with a supported one (or `JsonElement`/`object` if it really is any JSON); for a dangling `$ref`, ship the missing definition in the contract's types. |
| `RIV2006` | Error | A generic type parameter reaches the schema unbound, so there's nothing to emit for it. | Use a closed generic (`Wrapper<OrderDto>`, not `Wrapper<T>`), and give every instantiation one type argument per template parameter. |
| `RIV2007` | Error | A generic instantiation's template is absent from the contract's type definitions. | Ship the template (with its `typeParameters`) in the contract's types. |
| `RIV2008` | Error | Contract JSON declares one brand name two different ways; the message shows both declarations. | Declare every use of the brand identically, or give the second shape a brand name of its own. |
| `RIV2009` | Warning | Header parameter named `Accept`, `Content-Type` or `Authorization` — OpenAPI forbids these as header parameters; the parameter is omitted from the spec. | Describe content negotiation via media types and auth via security schemes; use a custom header name for anything else. |
| `RIV2010` | Error | Controller metadata (a repeated `[ProducesResponseType]` status) or contract JSON declares the same response status more than once. | Declare each status once; when one status genuinely has several payload shapes, declare one union type (`[RivetUnion]` in C#) and use that. |
| `RIV2011` | Error | Two `--security` values define the same scheme name. | Give each `--security` scheme a unique name (`name=<value>`). |
| `RIV2012` | Error | Two incompatible endpoints declare the same normalized HTTP method + route (e.g. contradictory response shapes from different extraction frontends) — generation fails naming both sources instead of writing a lossy spec. | Resolve the contradiction at the source. Equivalent duplicates collapse silently when contracts from several frontends are merged; within one contract or a `--from` document they still warn with `RIV2003`. |
| `RIV2013` | Error | Two routes differ only in their parameter names (`/items/{id}` and `/items/{itemId}`). OpenAPI identifies a path by its template with the names erased, so they're one path, and writing both makes an invalid document. Generation fails naming both. | Use the same parameter names on every route with that template. |

## RIV3xxx — import

Import warnings are also surfaced programmatically on
`ImportResult.Warnings`, where each entry carries its ID as a `RIV3001: `
prefix. The test-suite ratchet categories these map to are listed in the
[Import Profile](/reference/import-profile#warnings-stderr-importresult-warnings).

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV3001` | Warning | Alias schema is part of a `$ref` cycle — replaced with an empty schema; consumers resolve to an untyped object. | Break the cycle in the source spec, or type the affected members after scaffolding. |
| `RIV3003` | Warning | TRACE operation dropped — the HTTP method has no contract representation. | No action; TRACE is out of scope for the contract model. |
| `RIV3005` | Warning | `discriminator` with no reversible polymorphic shape (plain object without `oneOf`, or `oneOf` whose `mapping` is absent/unusable) — imported without dispatch semantics. | Model the polymorphism as a `oneOf` with a complete `discriminator.mapping` whose variants carry the tag property, or accept the non-polymorphic import. |
| `RIV3006` | Warning | Alias schema references a missing schema — consumers fall back to `JsonElement`. | Fix the dangling `$ref` in the source spec, or type the member after scaffolding. |
| `RIV3008` | Warning | Reference to an unresolvable alias schema (cycle or missing target) — using `JsonElement`. | Fix the alias it references (see `RIV3001`/`RIV3006`). |
| `RIV3009` | Warning | Schema could not be resolved to a C# type — mapped to `JsonElement`. | Type the member by hand in the scaffolded C#. |
| `RIV3010` | Warning | Unhandled JSON Schema `type` — mapped to `JsonElement`. | Type the member by hand in the scaffolded C#. |
| `RIV3011` | Warning | Array schema without `items` — mapped to `List<JsonElement>`. | Add `items` to the source spec, or type the list element after scaffolding. |
| `RIV3012` | Warning | Enum constraint that cannot become a C# enum (single-value, mixed/float, out-of-int32-range) — degraded to a primitive. | Accept the primitive, or hand-model the constraint in the scaffolded C#. |
| `RIV3014` | Warning | Dictionary `propertyNames` schema has no C# dictionary-key representation — imported with `string` keys. | Reference a string enum / string-backed brand or use a string key schema in the source spec, or type the dictionary key by hand after scaffolding. |
| `RIV3015` | Warning | Named scalar component uses const, composition, heterogeneous leaves, or a non-flat enum outside bounded scalar preservation. | Replace it with one primitive leaf (optionally nullable) or a flat string/Int32 enum; the existing fallback mapping is retained. |
| `RIV3020` | Warning | Parameter has an empty name, so the invalid parameter is dropped while the operation and its other parameters are preserved. | Give the source parameter a non-empty OpenAPI name. |
| `RIV3021` | Warning | Imported operation declares `Content-Type` as a header parameter even though OpenAPI represents request media types through `requestBody.content`. | Remove the reserved header parameter and retain the corresponding request-body media type. |
| `RIV3022` | Warning | Imported operation declares `Authorization` as a header parameter even though OpenAPI represents authentication through security schemes. | Remove the reserved header parameter and retain the corresponding operation or document security requirement. |
| `RIV3023` | Warning | Imported operation declares `Accept` as a header parameter even though OpenAPI represents response media types through response content. | Remove the reserved header parameter and retain the corresponding response media type. |
| `RIV3024` | Warning | Imported operation authors response content or an example on a body-forbidden status (1xx/204/205/304); the content is dropped while the status, description and headers are preserved. | Remove the body from the forbidden status in the source specification — HTTP forbids a message body on 1xx/204/205/304. |
| `RIV3025` | Warning | Parameter declares `content` instead of a `schema` (or neither) — the parameter is dropped from the operation. | Give the parameter a schema (a JSON-serialised value is a `string` parameter), or move the structured value into the request body. |
| `RIV3027` | Warning | Operation or component `callbacks` entry dropped — contracts describe requests the API receives, not ones it sends. | Document the outgoing call in the operation description, or give the receiving service its own contract. |
| `RIV3028` | Warning | `webhooks` or `components.pathItems` entry dropped — only path items under `paths` become contract endpoints. | Give the webhook's receiver its own contract; reference a reusable path item from `paths` if its operations belong to this API. |
| `RIV3029` | Warning | A path repeats an earlier path's template under other parameter names (`/deployments/{idOrUrl}` and `/deployments/{id}`), which OpenAPI forbids. Its operations merge into the earlier path and adopt its path parameter names; requests are unchanged, since path parameter names never reach the wire. Twins that share a method, carry path-level fields or reference the renamed parameter are left alone, and generation refuses them with `RIV2013`. | Give every operation on one template the same path parameter names in the source specification. |

## RIV4xxx — coverage (`--check`)

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV4001` | Warning | Contract endpoint has no matching implementation. | Implement the endpoint, or remove it from the contract. |
| `RIV4002` | Warning | Implementation HTTP method differs from the contract's. | Align the implementation's HTTP method with the contract. |
| `RIV4003` | Warning | Implementation route differs from the contract's. | Align the implementation's route with the contract. |
| `RIV4004` | Warning | A contract endpoint is bound in a route handler, but the handler neither returns a terminal built from that binding nor passes the binding to a helper whose answer it returns. | Return a `Success`, `Error`, or `File` terminal through the correct host adapter, pass the bound endpoint to a helper whose answer the handler returns, or remove the unused `Bind`. |
| `RIV4005` | Error | A handler implementing a contract endpoint binds a string-wire enum from route, query, header or form, directly or as a member of a complex type the host binds member by member (a minimal-API `[AsParameters]` or `[FromForm]` type; an MVC complex type with an explicit `[FromQuery]`/`[FromRoute]`/`[FromForm]`/`[FromHeader]`, without `[ApiController]`, with `SuppressInferBindingSourcesForParameters`, or under `[ApiController]` when one of its members carries its own binding source such as `[FromQuery]` or an `IFormFile`), at any depth, and the host would refuse an advertised wire value. That covers an enum, `T?` or a collection of either. `[FromBody]`, `[FromServices]` and, on MVC, `[ModelBinder]` exempt it. An unattributed collection is a JSON body (on MVC, under `[ApiController]`), except an array on a minimal-API endpoint mapped to a body-less verb such as GET, written in capitals the way `MapGet` passes it. Minimal APIs parse with `Enum.TryParse`, which needs the exact member name (the `[FromForm]` complex-type mapper ignores case); MVC ignores case; the Azure Functions worker parses a route value as JSON, so it takes no name at all. Either way the JSON converter is ignored. | Use a plain enum (integers on the wire), the built-in `JsonStringEnumConverter<T>` without renames, or bind a `string` and parse it in the handler. On MVC, camel-case and lowercase policies also bind. On Azure Functions only a plain enum or a `string` binds. |
| `RIV4006` | Error | A handler implementing a contract endpoint binds its inputs differently from how the contract declares them: it binds a route value, query parameter, header, form field or body the contract doesn't declare; never binds one the contract declares; or binds one under the declared name from another source. Names compare ignoring case, as ASP.NET's do. Binding follows the host's own inference (minimal API, MVC with and without `[ApiController]`, `[AsParameters]`, `Name =` overrides). A handler that can read the request itself (an `HttpContext`/`HttpRequest` parameter, a `BindAsync` type, a custom model binder, an MVC action using `Request` or `HttpContext`) is never told an input is unbound. The `Authorization` header and a `.QueryAuth()` token belong to security. Azure Functions handlers are not compared. | Bind each input where the contract declares it, under its wire name (`[FromQuery(Name = "q")]` for a `[JsonPropertyName("q")]` property), or change the contract's input type to say what the handler really takes. |

## Retired IDs

Retired IDs are never reused; they keep a tombstone here instead of a table row.

- ~~`RIV1005`~~ (`FromHeaderParameterExcluded`) — retired in P2 wave 5. `[FromHeader]`
  parameters used to be excluded from the contract with this warning; they now map to a
  first-class header parameter source (`in: header` in the emitted spec), so the
  diagnostic no longer exists.
- ~~`RIV1009`~~ (`UnsupportedTimeSpan`) — retired in the gap audit. `TimeSpan` used to
  emit an untyped (empty) schema with this warning; it now maps to the `string`
  System.Text.Json writes (the invariant `"c"` format, `[-][d.]hh:mm:ss[.fffffff]`) with
  `x-rivet-csharp-type: TimeSpan`, so the diagnostic no longer exists.
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
- ~~`RIV3002`~~ (`ImportSecuritySchemesDropped`) — retired in the gap audit. It promised
  that only the first security scheme survived import; nothing has emitted it since
  0.41.0, when the importer started keeping every alternative, AND-group and scope. Those
  now land in generated C# as `.Secure(...)`, `.AndSecure(...)` and `.OrAnonymous()`, so
  the warning had nothing left to warn about.
- ~~`RIV3004`~~ (`ImportAdditionalPropertiesDropped`) — retired in 0.42.0. A named object
  declaring both `properties` and `additionalProperties` used to lose its extension data
  with this warning; it now keeps typed properties plus extension data, so nothing is
  dropped.
- ~~`RIV3007`~~ (`ImportAliasRefCycle`) — retired in 0.45.0. Nothing ever emitted it: alias
  `$ref` cycles are broken before mapping and reported as `RIV3001`.
- ~~`RIV3013`~~ (`ImportDeclaredPropertiesDropped`) — retired in 0.42.0. An inline object
  declaring both `properties` and `additionalProperties` used to lose its typed properties
  with this warning; it now keeps both, so nothing is dropped.
