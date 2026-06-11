# Runtime Validation

This page is the honest scope statement: what Rivet guarantees at spec time, what
its runtime helpers enforce, and — just as important — what they do **not** enforce.

## Spec-time guarantees

These hold whenever you regenerate the spec (`dotnet rivet --project … --output …`):

- The emitted OpenAPI 3.1 spec is derived from the compiled C# via the Roslyn
  semantic model — types, routes, nullability, and status codes cannot drift from
  the code without the spec changing too.
- Constraint attributes (`[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`,
  `[RegularExpression]`, `[RivetConstraints]`, ...) are emitted as JSON Schema
  constraints (`minimum`, `maxLength`, `pattern`, `multipleOf`, ...).
- `--check` verifies that every contract field has an implementation and that the
  implementation's route and HTTP method match the declaration.

## Runtime guarantees (contract `Invoke`)

When controllers execute contracts through `.Invoke()`:

- **Input/output types are compiler-enforced.** The handler's parameter and return
  types are the contract's generic type arguments; a mismatch is a compile error.
- **Status codes are validated on the typed-results path.** When the handler returns
  ASP.NET `Results<...>`, Rivet throws `InvalidOperationException` at request time if
  the returned status code is neither the declared success status nor a status
  declared via `.Returns(...)`, or if the returned payload's **C# type** is not
  assignable to the type declared for that status. `.SkipValidation()` opts an
  endpoint out (needed for framework results like `ChallengeHttpResult` that carry
  no status code). Note the failure mode: the exception propagates through ASP.NET's
  normal unhandled-exception path, which in production means a 500 with no body.
- **The plain `Invoke` path fixes the status code.** `RivetResult`/`RivetResult<T>`
  carry the declared success status; the payload type is checked only by the
  compiler.

## What is NOT enforced at runtime

Do not rely on Rivet for any of the following — none of it happens:

- **Response body shape.** Rivet never inspects or filters serialized output. If the
  object you return has extra properties (e.g. you return a derived type, or your
  serializer includes members the spec does not declare), those properties go to the
  wire. The typed-results check is C# type assignability, not JSON shape validation.
- **Validation constraints.** Rivet's `Invoke` performs no constraint validation on
  requests or responses. Enforcement is the host framework's job — see
  [Enforcing constraints at runtime](#enforcing-constraints-at-runtime) below for
  the recipes.
- **`Define.File` endpoints.** `FileRouteDefinition` has no `Invoke` and therefore no
  runtime enforcement: content type, stream contents, and status codes are entirely
  up to your handler.
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
