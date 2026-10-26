# Diagnostics Reference

Every warning Rivet writes to stderr, and every error on this page, carries a stable ID in a canonical,
machine-parseable format:

```
warning RIV1019: <message>
error RIV2002: <message>
```

IDs are stable across releases — grep, baseline, or suppress by ID, never by
message text. A **warning** lets processing carry on; an **Error** is fatal and exits
`1`. There are slightly more errors than warnings these days, because most of what used
to be a warning about a wrong spec now refuses to write one. The errors are
`RIV1001`, `RIV1002`, `RIV1004`, `RIV1006`, `RIV1007`, `RIV1010`, `RIV1014`, `RIV1017`, `RIV1018`, `RIV1020`, `RIV1021`, `RIV1022`, `RIV1023`, `RIV1100`, `RIV1102`, `RIV1103`, `RIV1104`, `RIV1106`, `RIV1107`, `RIV1108`, `RIV1109`, `RIV1110`, `RIV1111`, `RIV1112`, `RIV1113`, `RIV1114`, `RIV1115`, `RIV1116`, `RIV2001`, `RIV2002`, `RIV2005`, `RIV2006`, `RIV2007`, `RIV2008`, `RIV2009`, `RIV2010`, `RIV2011`, `RIV2012`, `RIV2013`, `RIV4005`, `RIV4006`. Coverage
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
| `RIV1002` | Error | Response example targets a status code the contract endpoint does not declare — the spec has no response to put it on, so generation fails rather than quietly losing it. Also raised when an example names a media type its `.ResponseContent(...)` status doesn't declare, or names none when that status declares several. An example without a media type takes the status's own (or the success body's `.ProducesContentType(...)`). | Declare the status on the endpoint (`.Returns(...)`, `.Status(...)` or `.ResponseContent(...)`) or fix the example's status code. |
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
| `RIV1017` | Error | `.WithResponseHeader()` targets a status code the contract endpoint does not declare — the spec has no response to put it on, so generation fails rather than leaving out a header the handler sends. | Declare the status on the endpoint (`.Returns(...)`, `.Status(...)` or `.ResponseContent(...)`) or fix the header's status code. |
| `RIV1018` | Error | `[RivetUnion]` wrapper has no variant properties, so there is no union to emit. | Give the wrapper one optional property per variant (the shape the importer generates), or remove the attribute. |
| `RIV1019` | Warning | Route token has no matching property on the endpoint's input type (after normalized matching: case-insensitive, `_`/`-` stripped) — emitted as an untyped string path param. | Add a matching property to the input type (any casing of the token's words), or accept the untyped string param. |
| `RIV1020` | Error | The input type on a request with no JSON body (GET, DELETE, `.AcceptsBinary()`) is a dictionary, collection or scalar. Such an input must lower to route and query params, and this one has no properties to lower, so generation fails instead of emitting an operation without it. | Model the input as a record with one property per route or query param, or send the values as a JSON body with `Define.Post/Put/Patch`. |
| `RIV1021` | Error | An authored contract declares the same response status more than once, including a `.Returns(...)` collision with the success status. Such a contract fails at runtime and generation therefore aborts. | Declare a `[RivetUnion]` type if the status genuinely returns multiple shapes and return it once; otherwise remove the duplicate (often one `.Returns(...)` is mistyped). |
| `RIV1022` | Error | `[RivetRequestBody]` names a body type that is not represented independently by the endpoint input type. | Use the importer-generated marker and composite input, or remove the invalid hand-authored override. |
| `RIV1023` | Error | Imported generated C# changed while raw schema provenance still targets its original typed shape. | Re-import the OpenAPI document, or remove/update the stale raw schema provenance before emitting. |
| `RIV1100` | Error | A parameter's binding source cannot be established from explicit declarations or the retained narrow conventions (exact route-token name match, `CancellationToken`, `IFormFile`) — the contract is refused rather than emitting an operation that silently omits the input. Also raised, instead of the operation vanishing, when a `[RivetEndpoint]` method or an abstract method on a `[RivetContract]` class has no HTTP verb, or when one of those or a `[RivetClient]` endpoint has no route template on the action or its class. (A `[RivetClient]` method with no verb is a helper and is left alone.) | Give the parameter an explicit binding (`[FromBody]`, `[FromQuery]`, `[FromRoute]`, `[FromHeader]`, `[FromForm]`, or `[FromServices]` for DI plumbing), or remove it from the action. For a missing verb or route, add the `[Http*]` attribute and a route template, or remove the method. |
| `RIV1102` | Error | A response example or response content is authored on a body-forbidden status (1xx, 204, 205, 304) — HTTP forbids a message body on those statuses, so the authored content could never reach the wire. Reported at frontend parsing and again at emission as defense in depth; generation fails before any output is written. | Move the example/content to a status that allows a body, or remove it; the ordinary bodyless 204 response needs no example. |
| `RIV1103` | Error | A `[RivetScalar]` type is not a non-generic class/struct/record with one public readable `Value` property and a matching public constructor, without inheritance, competing converters or property-level JSON settings. | Give the type exactly one `Value` property (the Value type determines the scalar wire type), or remove `[RivetScalar]` if the type is an ordinary object. |
| `RIV1104` | Error | An endpoint mixes separate form fields or files with an explicit body parameter — a `[FromBody]` JSON body or a `[FromForm]` DTO whose recursive form shape Rivet cannot faithfully lower without emulating MVC's recursive form binder — the contract is refused instead of silently dropping the declared input. | Split the form DTO into scalar fields (each scalar emits a form part beside the file), move a `[FromBody]` body to a separate JSON endpoint, or accept the file-only surface. |
| `RIV1106` | Error | Two enum members produce the same wire value (via the declared converter's casing or duplicate `[JsonStringEnumMemberName]` pins) — generation fails instead of emitting an incorrect numeric schema. | Rename one of the colliding members (or fix its `[JsonStringEnumMemberName]`) so the wire values are distinct. |
| `RIV1108` | Error | Unsupported enum converter, wrong generic enum target, or string flags/aliases. | Use a built-in or Rivet enum converter with distinct values; use numeric serialization for flags. |
| `RIV1107` | Error | An endpoint declares the same response status twice with different bodies — a `[ProducesResponseType]`/`.Returns(...)` declaration and a mapped `Results<>` branch disagree on the payload type, or one declares a body where the other declares none. One status carries exactly one shape; generation fails instead of silently letting the attribute win. | Align the attribute declaration and the `Results<>` branch (same payload type, or both bodyless), or drop the redundant declaration. |
| `RIV1109` | Error | A contract endpoint declares the same response header more than once for one status. Header names are case-insensitive, so `.WithResponseHeader("ETag").WithResponseHeader("etag")` is a duplicate; generation fails instead of emitting both spellings. | Keep one `.WithResponseHeader(...)` per header name and status. |
| `RIV1110` | Error | A `[RivetEndpoint]` or `[RivetClient]` action binds a string-wire enum (a `Rivet*EnumConverter` or a `[JsonStringEnumMemberName]` rename) from route, query, header or form, directly or inside a complex parameter the host binds member by member, and the host would refuse a value the spec advertises. ASP.NET parses these itself and never asks the JSON converter: an MVC action needs each wire value to equal its member name ignoring case, a static `[RivetEndpoint]` (a minimal-API handler) needs it exactly. Which parameters count, host by host, is in [Limitations](/misc/limitations#enum-parameters-are-bound-by-asp-net). | Use a plain enum, the built-in `JsonStringEnumConverter<T>` without renames, or bind a `string` and parse it yourself. On MVC, camelCase and lowercase converters also bind, and an explicit `[ModelBinder]` exempts the parameter. |
| `RIV1111` | Error | A `[JsonConverter]` Rivet can't model sits on a walked type, property or field: anything but a supported converter on the enum type, `[RivetScalar]` or `[RivetUnion]`. The wire is whatever the converter writes, so the spec would describe the CLR shape instead. | On a primitive-typed property, pin the wire with `[RivetSchemaType("string")]` (or whichever JSON type the converter writes). Otherwise make the value a `[RivetScalar]` wrapper, or put a supported converter on the enum type itself. |
| `RIV1112` | Error | `[JsonNumberHandling]` with `WriteAsString` or `AllowNamedFloatingPointLiterals` reaches a number, or a collection of numbers, on a walked type, from the member or from the type being serialized. That includes `Int128`, `UInt128` and `Half`. A generic's `T` member is judged per instantiation: `Wrapper<int>` is refused, `Wrapper<string>` is fine. Strings go on the wire where the spec says number. `AllowReadingFromString` alone is accepted: numbers are still written as numbers, it only widens what the host reads. | Remove it, pin a primitive-typed property with `[RivetSchemaType("string")]`, or instantiate the generic with a non-number type argument. |
| `RIV1113` | Error | Rivet reads a contract endpoint from source and can't read this one: the field has no initializer, or its initializer isn't a `Define.*` builder chain (a helper call or another field, say); the endpoint is a property rather than a field; the chain calls something that isn't a Rivet builder method; an example has nothing in it; or a route is `null`; or a builder call is given a string, int or bool that isn't a compile-time constant. That last one covers every such argument, not just the route: `.Summary(text)`, `.Status(code)`, `.ProducesFile(type)`, `.QueryAuth(name)`, a scheme name, a scope. Rivet can't tell a variable from an omitted argument, so it used to skip the field or quietly fall back to the default. | Declare the endpoint as a `public static readonly` field initialised with `Define.*` and Rivet builder calls only, and pass every argument as a literal or a `const` (const strings can be concatenated or interpolated). |
| `RIV1114` | Error | A contract endpoint's security calls contradict each other: `.Anonymous()` (no security requirement at all) sits alongside `.Secure()` or `.OrAnonymous()`, or `.AndSecure()` has no `.Secure()` alternative to join (it comes first, or straight after `.OrAnonymous()`). Rivet refuses rather than guessing which call you meant. | Use `.Anonymous()` alone for an open endpoint, `.Secure(...)` plus `.OrAnonymous()` for optional credentials, and start every AND-group with `.Secure(...)`. |
| `RIV1115` | Error | Something in the app assigns `ApiBehaviorOptions.SuppressInferBindingSourcesForParameters` a value that isn't a compile-time constant (a config read, a variable, a compound assignment). Whether MVC infers binding sources for `[ApiController]` actions decides what every MVC parameter binds, so `RIV1110`, `RIV4005` and `RIV4006` would be answering on a guess; generation and `--check` stop instead, naming the file and line. The setting is read only when it matters: when an annotated MVC action (generation) or a contract's MVC implementation (`--check`) is on an `[ApiController]` controller. | Assign the constant `true` or `false` (a `const bool` works too). |
| `RIV1116` | Error | A walked type has more than one `[JsonConstructor]` constructor. System.Text.Json refuses such a type at runtime, so there's no constructor whose parameters the spec could follow. | Keep `[JsonConstructor]` on one constructor. |

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
| `RIV2005` | Error | A type with no schema mapping (an `unknown` primitive whose `csharpType` isn't one of the "any JSON" types below), or a `$ref` to a schema component nothing defines. The message names the type and its site, or the dangling component and where it's referenced. The check reads the finished document; a `"$ref"` key inside an example's `value`, a link's `requestBody` or `parameters`, or a default, const, enum or `x-` extension is data, not a reference. "Any JSON value" is not this: TypeScript's `unknown`, `object`, `JsonElement`, `JsonDocument` and the `JsonNode` family (`JsonNode`, `JsonObject`, `JsonArray`, `JsonValue`) map to the empty schema `{}` with no diagnostic at all. (A C# `JsonObject` or `JsonArray` property is an object or array, as Type Mapping says; the empty schema is for contract JSON naming one as an `unknown`'s `csharpType`.) | Replace the named type with a supported one (or `JsonElement`/`object` if it really is any JSON); for a dangling `$ref`, ship the missing definition in the contract's types. |
| `RIV2006` | Error | A generic type parameter reaches the schema unbound, so there's nothing to emit for it, or an instantiation's type-argument count doesn't match its template's. | Use a closed generic (`Wrapper<OrderDto>`, not `Wrapper<T>`), and give every instantiation one type argument per template parameter. |
| `RIV2007` | Error | A generic instantiation's template is absent from the contract's type definitions. | Ship the template (with its `typeParameters`) in the contract's types. |
| `RIV2008` | Error | Contract JSON declares one brand name two different ways; the message shows both declarations. | Declare every use of the brand identically, or give the second shape a brand name of its own. |
| `RIV2009` | Error | Header parameter named `Accept`, `Content-Type` or `Authorization` — OpenAPI ignores these as header parameters, so the spec would never carry it and clients would never send what the handler reads; generation fails. | Describe content negotiation with `.ProducesContentType(...)`/`.AcceptsContentType(...)` and auth with a security scheme (`.Secure(...)` or `--security`); use a custom header name for anything else. |
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
| `RIV3005` | Warning | `discriminator` with no reversible polymorphic shape (plain object without `oneOf`, or `oneOf` whose `mapping` is absent/unusable) — imported without dispatch semantics. Also a single `discriminator.mapping` entry that points into another file (`models/resize.yml`): Rivet imports one document, so that entry is dropped and the rest of the discriminator kept. | Model the polymorphism as a `oneOf` with a complete `discriminator.mapping` whose variants carry the tag property, or accept the non-polymorphic import. For a cross-file mapping, bundle the spec into one document first. |
| `RIV3006` | Warning | Alias schema references a missing schema — consumers fall back to `JsonElement`. | Fix the dangling `$ref` in the source spec, or type the member after scaffolding. |
| `RIV3008` | Warning | Reference to an unresolvable alias schema (cycle or missing target) — using `JsonElement`. | Fix the alias it references (see `RIV3001`/`RIV3006`). |
| `RIV3009` | Warning | Schema could not be resolved to a C# type — mapped to `JsonElement`. | Type the member by hand in the scaffolded C#. |
| `RIV3010` | Warning | Unhandled JSON Schema `type` — mapped to `JsonElement`. | Type the member by hand in the scaffolded C#. |
| `RIV3011` | Warning | Array schema without `items` — mapped to `List<JsonElement>`. | Add `items` to the source spec, or type the list element after scaffolding. |
| `RIV3014` | Warning | Dictionary `propertyNames` schema has no C# dictionary-key representation — imported with `string` keys. | Reference a string enum / string-backed brand or use a string key schema in the source spec, or type the dictionary key by hand after scaffolding. |
| `RIV3015` | Warning | Named scalar component uses `const`, schema composition, or heterogeneous leaf types. | Replace it with one primitive leaf (optionally nullable); the existing fallback mapping is retained. |
| `RIV3019` | Warning | A Swagger 2 document gives a `host` with no `schemes`, or `schemes` with no `host`. Swagger fills the missing half from wherever the document was served, which an import can't know, so Rivet imports no server rather than invent a URL. | Give the source both `host` and `schemes`, or pass the real server to generation with `--server`. |
| `RIV3020` | Warning | Parameter has an empty name, so the invalid parameter is dropped while the operation and its other parameters are preserved. | Give the source parameter a non-empty OpenAPI name. |
| `RIV3021` | Warning | Imported operation declares `Content-Type` as a header parameter even though OpenAPI represents request media types through `requestBody.content`. | Remove the reserved header parameter and retain the corresponding request-body media type. |
| `RIV3022` | Warning | Imported operation declares `Authorization` as a header parameter even though OpenAPI represents authentication through security schemes. | Remove the reserved header parameter and retain the corresponding operation or document security requirement. |
| `RIV3023` | Warning | Imported operation declares `Accept` as a header parameter even though OpenAPI represents response media types through response content. | Remove the reserved header parameter and retain the corresponding response media type. |
| `RIV3024` | Warning | Imported operation authors response content or an example on a body-forbidden status (1xx/204/205/304); the content is dropped while the status, description and headers are preserved. | Remove the body from the forbidden status in the source specification — HTTP forbids a message body on 1xx/204/205/304. |
| `RIV3025` | Warning | Parameter declares `content` instead of a `schema` (or neither) — the parameter is dropped from the operation. | Give the parameter a schema (a JSON-serialised value is a `string` parameter), or move the structured value into the request body. |
| `RIV3027` | Warning | Operation or component `callbacks` entry dropped — contracts describe requests the API receives, not ones it sends. | Document the outgoing call in the operation description, or give the receiving service its own contract. |
| `RIV3028` | Warning | `webhooks` or `components.pathItems` entry dropped — only path items under `paths` become contract endpoints. | Give the webhook's receiver its own contract; reference a reusable path item from `paths` if its operations belong to this API. |
| `RIV3029` | Warning | A path repeats an earlier path's template under other parameter names (`/deployments/{idOrUrl}` and `/deployments/{id}`), which OpenAPI forbids. Its operations merge into the earlier path and adopt its path parameter names; requests are unchanged, since path parameter names never reach the wire. Twins that share a method, carry any path-level field on either path, repeat a parameter name, or have an operation that `$ref`s a renamed path parameter are left alone, and generation refuses them with `RIV2013`. | Give every operation on one template the same path parameter names in the source specification. |
| `RIV3030` | Warning | A security alternative names a scheme the document never defines (`components.securitySchemes`, or Swagger 2's `securityDefinitions`). Nobody can present that scheme, so the alternative is dropped, which changes nothing about who gets in. When an operation's own security, or the document security it inherits, has no alternative left, nobody can call the operation, so it is dropped too, rather than imported as `security: []`, which would read as "no credentials needed". An operation that inherits is kept when import `--security` names a scheme for operations that declare none. | Define the scheme under `components.securitySchemes`, or fix the requirement's name in the source spec. |
| `RIV3031` | Warning | An integer enum lists a whole value its C# integer type can't hold (`[-1, 18446744073709551615]` has no CLR integer that fits both, so it stays a `long`). The values still travel in the generated metadata and re-emit exactly, but C# code reading or writing that type can't carry the values the warning names. | Narrow the enum to one integer range in the source spec, give it a `format` whose type holds every value, or pin a wider type with `x-rivet-csharp-type` (`Int128`). |

## RIV4xxx — coverage (`--check`)

| ID | Severity | Trigger | Remediation |
|---|---|---|---|
| `RIV4001` | Warning | Contract endpoint has no matching implementation. | Implement the endpoint, or remove it from the contract. |
| `RIV4002` | Warning | Implementation HTTP method differs from the contract's. | Align the implementation's HTTP method with the contract. |
| `RIV4003` | Warning | Implementation route differs from the contract's. | Align the implementation's route with the contract. |
| `RIV4004` | Warning | A contract endpoint is bound in a route handler, but the handler neither returns a terminal built from that binding nor passes the binding to a helper whose answer it returns. | Return a `Success`, `Error`, or `File` terminal through the correct host adapter, pass the bound endpoint to a helper whose answer the handler returns, or remove the unused `Bind`. |
| `RIV4005` | Error | A handler implementing a contract endpoint binds a string-wire enum (an enum, `T?` or a collection of either) from route, query, header or form, directly or inside a complex type the host binds member by member, and the host would refuse a value the spec advertises. ASP.NET parses these itself and never asks the JSON converter: a minimal API needs the exact member name, MVC ignores case, and the Azure Functions worker reads a route value as JSON, so it takes no name at all. Parameters the host reads as a JSON body, `[FromServices]` and, on MVC, `[ModelBinder]` are left alone. Which parameters count, host by host, is in [Limitations](/misc/limitations#enum-parameters-are-bound-by-asp-net). | Use a plain enum, the built-in `JsonStringEnumConverter<T>` without renames, or bind a `string` and parse it yourself. On MVC, camelCase and lowercase converters also bind. On Azure Functions only a plain enum or a `string` does. |
| `RIV4006` | Error | A handler implementing a contract endpoint binds its inputs differently from how the contract declares them: it binds a route value, query parameter, header, form field or body the contract doesn't declare; never binds one the contract declares; or binds one under the declared name from another source. Names compare ignoring case, as ASP.NET's do. Binding follows the host's own inference (minimal API, MVC with and without `[ApiController]`, `[AsParameters]`, `Name =` overrides). A `.FormEncoded()` body is compared field by field under its wire names. A declared cookie is reported unless the handler reads the request, since no attribute binds one. A handler that can read the request itself (an `HttpContext`/`HttpRequest` parameter, a `BindAsync` type, a custom model binder, or a body that reads `Request` or `RouteData` however it reaches them, calls `GetRouteValue`/`GetRouteData`, or hands the `HttpContext` or `HttpRequest` to another method) is never told an input is unbound; reading only `User`, `Response` or `RequestAborted` doesn't count. The `Authorization` header, a `.QueryAuth()` token and the header or query parameter of an apiKey scheme in the endpoint's (or the document's global) security, from attributes or `--security`, belong to security. Azure Functions handlers are not compared. | Bind each input where the contract declares it, under its wire name (`[FromQuery(Name = "q")]` for a `[JsonPropertyName("q")]` property), or change the contract's input type to say what the handler really takes. |

## Retired IDs

Retired IDs are never reused; they keep a tombstone here instead of a table row.

- ~~`RIV1005`~~ (`FromHeaderParameterExcluded`) — retired in P2 wave 5. `[FromHeader]`
  parameters used to be excluded from the contract with this warning; they now map to a
  first-class header parameter source (`in: header` in the emitted spec), so the
  diagnostic no longer exists.
- ~~`RIV1009`~~ (`UnsupportedTimeSpan`) — retired in 0.49.0. `TimeSpan` used to
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
- ~~`RIV1101`~~ (`JsonIncludeFieldNotRepresented`) — retired in 0.49.0. It was registered
  in 0.42.0 as a reserved slot for `[JsonInclude]` shapes the walker couldn't prove
  statically, but every shape it could meet (mixed accessors, private accessors,
  included fields) was already worked out, so nothing ever emitted it. A diagnostic
  that can't fire is a promise nobody checks, so it went.
- ~~`RIV1105`~~ (`RivetEnumNamingPolicyWithoutStringConverter`) — retired in the
  converter-family wave (0.44.0). The casing convention now lives in the enum's
  `[JsonConverter]` declaration itself (`RivetCamelCaseEnumConverter<T>` family), so a
  policy cannot exist without a string wire and the dangling-marker case is unexpressible.
- ~~`RIV3002`~~ (`ImportSecuritySchemesDropped`) — retired in 0.49.0. It promised
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
- ~~`RIV3012`~~ (`ImportEnumConstraintDropped`) — retired in 0.49.0. An enum that couldn't
  become a C# enum (one value, booleans, numbers, `[null]`, mixed or out-of-range values)
  used to lose its values with this warning, and an untyped one became a `string`. Every literal enum now keeps them: the C# type comes
  from the values (`{ enum: [true] }` is a `bool`), and the values travel in the generated
  metadata, so the spec re-emits them.
- ~~`RIV3013`~~ (`ImportDeclaredPropertiesDropped`) — retired in 0.42.0. An inline object
  declaring both `properties` and `additionalProperties` used to lose its typed properties
  with this warning; it now keeps both, so nothing is dropped.
