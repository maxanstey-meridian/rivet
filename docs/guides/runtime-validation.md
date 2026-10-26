# Runtime Validation

This page is the honest scope statement: what Rivet guarantees at spec time, what
its runtime helpers enforce, and — just as important — what they do **not** enforce.

## Spec-time guarantees

These hold whenever you regenerate the spec (`dotnet rivet --project … --output …`):

- The emitted OpenAPI 3.1 spec is deterministically derived from the transport
  shape declared in compiled C# via the Roslyn semantic model, serializer attributes
  on the types included. Serializer settings that would make the wire differ from
  that declaration don't get waved through: a `[JsonConverter]` or
  `[JsonNumberHandling]` Rivet can't model stops generation (`RIV1111`, `RIV1112`),
  and host JSON options that change the wire are refused when a result runs (below).
  Middleware that rewrites a response after the adapter is outside the declaration
  and outside the spec.
- Constraint attributes (`[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[Length]`,
  `[RegularExpression]`, `[RivetConstraints]`, ...) are emitted as JSON Schema
  constraints (`minimum`, `maxLength`, `pattern`, `multipleOf`, ...).
- `--check` verifies that every contract field has an implementation, that the
  implementation's route and HTTP method match the declaration, that it binds each
  input where the contract declares it (`RIV4006`), and that an enum it binds from the
  route, query, header or form can take every value the spec lists (`RIV4005`).
  Generation refuses an unbindable enum for an annotated action too (`RIV1110`).

## Runtime guarantees (contract responses)

At the transport boundary, application code binds input-bearing definitions, runs
normally, and constructs a contract-owned response:

- **Input/output types are compiler-enforced.** `.Bind(input)` accepts the contract's
  `TInput`, and the bound endpoint's `.Success(payload)` accepts its `TOutput`.
  Definitions without input call `.Success(...)` directly.
- **Status codes are contract-owned.** `.Success(...)` selects the declared success
  status. `.Error(status, ...)` throws `RivetContractViolationException` if the
  status was not declared via `.Returns(...)` (including matching imported `nXX`
  and `default` responses).
- **Payload runtime types are validated.** The supplied payload's C# type must be
  assignable to the declared type — and, because
  System.Text.Json serializes the **value's runtime type**, a derived instance where
  the contract declares a concrete type is rejected, whether the handler returned
  a derived value directly or upcast it to the declared type. This closes the
  extra-field leak: undeclared members on a subtype cannot reach the wire silently.
  Declared types that are interfaces or abstract accept their subtypes;
  `[JsonPolymorphic]` types accept subtypes registered with `[JsonDerivedType]`,
  matching the hierarchy emitted as `oneOf`.
- **Bodies and content types must match the declaration.** A content-bearing result
  on a status that declares no payload is a violation. Non-JSON textual responses
  require a `string` payload, and binary responses must use `.File(...)`.
- **File responses are contract-owned.** `.File(...)` requires a binary/file success
  response and takes its content type from the declaration. With multiple binary
  representations, pass `contentType:` to select one explicitly; undeclared
  selections fail. It accepts `byte[]`, a
  readable `Stream`, an absolute physical path, or a `Func<Stream, Task>` writer that
  fills the response as it goes; range-enabled streams must be seekable, and a writer
  never serves ranges. Input-bearing file definitions first use `.Bind(input)`.
- **Host adapters preserve the result.** The first-party `.ToActionResult()` and
  `.ToResult()` adapters write body, content type, file metadata, and status through
  MVC or minimal APIs. Files take one path on both hosts: ASP.NET's minimal-API file
  results, so ranges, conditional requests and disposition behave identically, and
  an MVC app's replacement file-result executors aren't used. If the host response has
  already started incompatibly or has established a conflicting status, they throw
  `RivetContractViolationException`.
  An MVC result filter can read the status (`IStatusCodeActionResult`) and the payload
  (`RivetActionResult.Value`) of what `.ToActionResult()` returned, but not change them.
  For a file that status is the declared one: a range or conditional request can still
  turn it into a 206, 304, 412 or 416 on the way out, so read
  `HttpContext.Response.StatusCode` in `OnResultExecuted` when you need what was sent.
- **Required response headers turn up.** A header declared with
  `.WithResponseHeader(..., required: true)` has to be on the response when the
  adapter writes it, or the adapter throws, naming the header and the status. Set it
  on `HttpContext.Response.Headers` before returning; a file's own arguments
  (`entityTag:`, `lastModified:`, `downloadName:`, `inline:`, `enableRangeProcessing:`)
  count for the headers they produce. A range status (`4XX`) holds every status in it.
  The [endpoint builder](/reference/endpoint-builder#runtime-responses) has the details.
- **The host's JSON options are the ones the spec describes.** The spec assumes
  System.Text.Json's web defaults: camelCase names, every member written, numbers as
  numbers, no global converters. So the first time an adapter runs against a given
  set of options, it compares them with those defaults and throws if anything would
  change the wire, naming every difference. `.ToResult()` checks the minimal-API
  options (`ConfigureHttpJsonOptions`, `Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>`),
  `.ToActionResult()` checks MVC's (`AddJsonOptions`, `Configure<Microsoft.AspNetCore.Mvc.JsonOptions>`).
  Every result is checked, bodyless and file ones too, because the request bodies on
  those endpoints are read with the same options.

  Refused: a naming policy other than camelCase (none at all included), a
  `DictionaryKeyPolicy`, anything in `Converters` (Rivet's own enum converters too:
  the spec only sees a converter declared on the type), `NumberHandling` that writes
  numbers as strings or named literals, a `DefaultIgnoreCondition` other than
  `Never`, `IgnoreNullValues`, `IgnoreReadOnlyProperties`, `IgnoreReadOnlyFields`,
  `IncludeFields`, a `ReferenceHandler`, `UnmappedMemberHandling.Disallow` and
  `RespectRequiredConstructorParameters` (both refuse requests the spec allows), and
  a `TypeInfoResolver` that isn't the default one or a source-generated
  `JsonSerializerContext`. That last one means resolver modifiers: they can rename,
  drop or add members, including polymorphism the spec never shows.

  Left alone, because no exchange that follows the spec comes out different:
  indenting, the encoder, `MaxDepth`, case-insensitive or case-sensitive property
  matching, trailing commas, comments, reading numbers from strings,
  `RespectNullableAnnotations`, `PreferredObjectCreationHandling`,
  `UnknownTypeHandling` and `AllowOutOfOrderMetadataProperties`. ASP.NET's own
  defaults pass on .NET 8, 9 and 10, including the source-generated context
  `AddProblemDetails()` adds.

  One reading setting is the other way round: a contract whose input is polymorphic
  needs it. System.Text.Json only recognises a discriminator (`$type`, or whatever
  `TypeDiscriminatorPropertyName` says) when it's the **first** property of its
  object. The spec can't say that, because JSON objects have no order, so a client
  that sends `{"radius": 2, "$type": "circle"}` is following the spec and gets
  refused for it. .NET 9 added `AllowOutOfOrderMetadataProperties`, which reads the
  discriminator wherever it turns up. So when a bound endpoint's `TInput` reaches a
  `[JsonPolymorphic]`/`[JsonDerivedType]` type anywhere in its graph (the body itself,
  a list of shapes, a dictionary of them three records down), the adapter throws when
  its result runs unless the host options switch that on, naming the type, where the
  input reaches it, and the setting:

  ```csharp
  builder.Services.ConfigureHttpJsonOptions(o =>
      o.SerializerOptions.AllowOutOfOrderMetadataProperties = true);   // .ToResult()
  builder.Services.AddControllers().AddJsonOptions(o =>
      o.JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true); // .ToActionResult()
  ```

  On .NET 8 there's no such setting, so the refusal there is unconditional: move the
  host to .NET 9 or later, or give the endpoint an input that isn't polymorphic.
  The same goes, on every runtime, for an input that never meets the serializer at
  all: a `.FormEncoded()` or `.AcceptsFile()` form, or the route and query of an
  `.AcceptsBinary()` endpoint. ASP.NET's model binding has no discriminator. It
  builds the declared type and never a variant, so no JSON setting can help, and the
  adapter says so.
  Responses don't care either way, since the serializer writes the discriminator
  first. A `[JsonPolymorphic]` type with no `[JsonDerivedType]` at all never gets this
  far: System.Text.Json won't read or write it on any runtime, so generation refuses
  it (`RIV1015`).

  Want snake_case, or enums as strings everywhere? Say it on the types:
  `[JsonPropertyName]`, `[JsonIgnore]`, `[JsonConverter(typeof(RivetCamelCaseEnumConverter<T>))]`
  and `[JsonPolymorphic]` all go into the spec, so the wire and the spec move together.

### The failure envelope

Every runtime check above throws `RivetContractViolationException` (a subclass of
`InvalidOperationException`, so existing catch blocks keep working). Unhandled,
that surfaces as ASP.NET's default 500 with no body. Register the bundled handler
to emit the structured Rivet envelope instead — the same `{ code, message }` shape
the rivet-ts Hono adapter uses for its enforcement failures, so both runtimes
report violations identically on the wire:

```csharp
builder.Services.AddExceptionHandler<RivetContractViolationHandler>();
builder.Services.AddProblemDetails(); // fallback for everything else
// ...
app.UseExceptionHandler();
```

A violation then returns
`500 { "code": "contract_violation", "message": "Route '/api/items/{id}' returned undeclared status code 409. ..." }`.

## What is NOT enforced at runtime

Do not rely on Rivet for any of the following — none of it happens:

- **Serialized JSON shape.** The runtime checks are type-identity checks on the CLR
  value and a comparison of the host's options, never inspection of serialized
  output. A derived instance is rejected, wire-changing host options are refused
  (see above), and a converter Rivet can't model never gets past generation. What's
  left is taken on trust: a member pinned with `[RivetSchemaType]` is whatever its
  converter writes, the pin is your word for it; whatever a `[JsonExtensionData]`
  dictionary holds goes out as extra members; and `null` in a required,
  non-nullable member is written as `null`, not caught.
- **Validation constraints.** Rivet's response terminals perform no constraint
  validation on requests or responses. Enforcement is the host framework's job —
  see [Enforcing constraints at runtime](#enforcing-constraints-at-runtime) below
  for the recipes.
- **Handlers that bypass the contract response terminals.** Nothing forces a route
  to return `.Success(...)`, `.Error(...)`, or `.File(...)`; contract↔route binding
  is `--check`'s job, not the runtime's. Stream *contents* are never inspected.
- **Request parsing and binding.** Done by ASP.NET (or whatever host you bridge to),
  not by Rivet. What Rivet does check is the shape of it, statically: `--check` lines
  the contract's inputs up against the handler's parameters and fails with `RIV4006`
  when the handler binds an input the contract doesn't declare, ignores one it does,
  or reads it from the wrong place (a header where the spec says query, `?Text=` where
  it says `?q=`). It reads binding the way the host does, inference included, so it
  catches the `[ApiController]` GET that quietly takes its query record from the body.
  It can't see a value the handler fishes out of `HttpRequest` by hand, and it doesn't
  look at Azure Functions, whose handlers do exactly that. See
  [Contract Coverage](./contract-coverage#inputs-the-handler-doesn-t-bind-where-the-contract-says).
- **Examples.** `.RequestExampleJson(...)` / `.ResponseExampleJson(...)` are runtime
  no-ops; Roslyn reads them at generation time only.
- **Request headers, and the values of response headers.** Declared request headers
  (`[RivetHeader]` / `[FromHeader]`) are bound by the host framework, not Rivet.
  Rivet never sets a response header either: emitting `Location`/`ETag`/`Retry-After`
  is handler code. It checks that a `required: true` header is there (see above), not
  what's in it.

## Enforcing constraints at runtime

The same constraint attributes that flow into the spec can be enforced by the
host — so the wire behaviour matches what the spec promises:

- **Controller hosts.** Add `[ApiController]` to the controller: ASP.NET model
  validation rejects invalid request DTOs before the action runs. The automatic
  response is a `400 ValidationProblemDetails` — if your contract declares a
  different error shape (e.g. `.Returns<ValidationErrorDto>(422)`), configure
  `ApiBehaviorOptions.InvalidModelStateResponseFactory` to match it. The
  `samples/ContractApi` project is the worked example: `[ApiController]` +
  a factory that reshapes `ModelState` into the declared 422 `ValidationErrorDto`.
- **Minimal-API hosts.** Run DataAnnotations yourself with
  `Validator.TryValidateObject(dto, context, results, validateAllProperties: true)`
  in an endpoint filter, returning your declared error shape on failure (this is
  the scaffolded-host pattern).
- **`[RivetConstraints]`** is a `ValidationAttribute`, so its two facets
  (`MultipleOf`, `UniqueItems`) participate in both of the above. Null values pass — pair with
  `[Required]`, per DataAnnotations convention.
- Exclusive bounds and item counts are plain DataAnnotations:
  `[Range(0, double.MaxValue, MinimumIsExclusive = true)]` and `[MinLength]`/`[MaxLength]`/`[Length]`
  on a collection. `[MinLength]` counts a value through `ICollection` or a `Count` property,
  so it validates deserialized collections (an `IEnumerable<T>` property holds a `List<T>`)
  but throws `InvalidCastException` for a lazy sequence.

### The positional-record gotcha

Rivet reads a constraint wherever you put it on a positional record: on the
property (`[property: Range(1, 100)]`) or on the constructor parameter (a plain
`[Range(1, 100)]`, which is where C# puts an attribute with no target). The spec
comes out the same either way. The hosts are fussier:

- On the **parameter**, MVC model validation enforces it, but
  `Validator.TryValidateObject` only looks at properties, so the minimal-API
  pattern waves the value through.
- On the **property**, `Validator.TryValidateObject` enforces it, but MVC model
  validation **throws `InvalidOperationException` at request time** ("validation
  metadata must be associated with the constructor parameter"), surfacing as a 500.

So for request DTOs, skip the positional form: declare the record with explicit
`init` properties and put the constraint attributes on them. That one placement
works for the spec, for MVC, and for `Validator.TryValidateObject` — see
`InviteMemberRequest` in `samples/ContractApi/Models/MemberModels.cs`.

The `--from-openapi` scaffold follows the same rule: any record whose properties
carry a `ValidationAttribute` (the DataAnnotations constraint set,
`EmailAddress`/`Url` from formats, or `[RivetConstraints]`) is generated in the
non-positional `required`/`init` form, so imported DTOs are safe under MVC model
validation out of the box. Unconstrained records stay positional.

## Validating at the network boundary

If you want runtime validation of payloads against the contract, generate it from
the emitted spec — that is the design: Rivet owns C# → spec, the OpenAPI ecosystem
owns codegen. Any OpenAPI-ecosystem validator generator works as input:
`./generated/openapi.json` carries the full constraint set, because the C#-side
constraint attributes flow into the spec as JSON Schema constraints. (Evaluate the
maintenance state of whichever generator you pick — several popular ones are
effectively unmaintained.)

> Historical note: the v1 `--compile`/`--jsonschema` flags that emitted `schemas.ts`
> and `validators.ts` were removed in v2.
