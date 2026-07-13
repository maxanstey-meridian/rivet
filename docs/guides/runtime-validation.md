# Runtime Validation

This page is the honest scope statement: what Rivet guarantees at spec time, what
its runtime helpers enforce, and — just as important — what they do **not** enforce.

## Spec-time guarantees

These hold whenever you regenerate the spec (`dotnet rivet --project … --output …`):

- The emitted OpenAPI 3.1 spec is deterministically derived from the transport
  shape declared in compiled C# via the Roslyn semantic model. Custom serializer
  settings, middleware and other runtime behaviour outside that declaration are
  not visible; known fidelity loss produces diagnostics where Rivet can detect it.
- Constraint attributes (`[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`,
  `[RegularExpression]`, `[RivetConstraints]`, ...) are emitted as JSON Schema
  constraints (`minimum`, `maxLength`, `pattern`, `multipleOf`, ...).
- `--check` verifies that every contract field has an implementation and that the
  implementation's route and HTTP method match the declaration.

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
  response and takes its content type from the declaration. It accepts `byte[]`, a
  readable `Stream`, or an absolute physical path; range-enabled streams must be
  seekable. Input-bearing file definitions first use `.Bind(input)`.
- **Host adapters preserve the result.** The first-party `.ToActionResult()` and
  `.ToResult()` adapters write body, content type, file metadata, and status through
  MVC or minimal APIs. If the host response has already started incompatibly or has
  established a conflicting status, they throw `RivetContractViolationException`.

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
  value, never inspection of serialized output. A derived instance is rejected (see
  above), but members your *serializer configuration* adds, renames, or drops on the
  declared type itself (custom converters, `[JsonExtensionData]`, contract
  customization) go to the wire unchecked, and `null` in a required member is not
  caught.
- **Validation constraints.** Rivet's response terminals perform no constraint
  validation on requests or responses. Enforcement is the host framework's job —
  see [Enforcing constraints at runtime](#enforcing-constraints-at-runtime) below
  for the recipes.
- **Handlers that bypass the contract response terminals.** Nothing forces a route
  to return `.Success(...)`, `.Error(...)`, or `.File(...)`; contract↔route binding
  is `--check`'s job, not the runtime's. Stream *contents* are never inspected.
- **Request parsing and binding.** Done by ASP.NET (or whatever host you bridge to),
  not by Rivet.
- **Examples.** `.RequestExampleJson(...)` / `.ResponseExampleJson(...)` are runtime
  no-ops; Roslyn reads them at generation time only.
- **Headers.** Declared request headers (`[RivetHeader]` / `[FromHeader]`) and
  response headers (`.WithResponseHeader(...)`) are *spec-only* — Rivet never binds,
  sets, or validates them. Reading a request header is host-framework binding;
  emitting `Location`/`ETag`/`Retry-After` is handler code. Declaring a response
  header `required: true` is a documentation promise your handler must keep.

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
- **`[RivetConstraints]`** is a `ValidationAttribute`, so its facets
  (`ExclusiveMinimum`, `ExclusiveMaximum`, `MultipleOf`, `MinItems`, `MaxItems`,
  `UniqueItems`) participate in both of the above. Null values pass — pair with
  `[Required]`, per DataAnnotations convention.

### The positional-record gotcha

Where the attribute *sits* on a record decides who can see it:

- On a positional record parameter **without** `[property:]`, the attribute lands
  on the constructor parameter: MVC model validation enforces it, but the Rivet
  spec cannot see it — the wire is stricter than the spec. Drift in one direction.
- **With** `[property:]`, the spec sees it and `Validator.TryValidateObject`
  enforces it (the minimal-API pattern works) — but MVC model validation
  **throws `InvalidOperationException` at request time** ("validation metadata
  must be associated with the constructor parameter"), surfacing as a 500. Drift
  in the other direction.

For request DTOs validated by controller hosts, avoid the positional form
entirely: declare the record with explicit `init` properties and put the
constraint attributes on the properties. That single placement is visible to the
spec, to MVC, and to `Validator.TryValidateObject` — see
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
