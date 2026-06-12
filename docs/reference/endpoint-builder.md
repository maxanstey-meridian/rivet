# Route Definition API

Contract endpoints are built with the `Define` factory and a fluent builder. Roslyn
reads the chain at generation time; the same object provides type-safe `Invoke` at
runtime.

## Factories

| Factory | Variants | Default success status |
|---|---|---|
| `Define.Get(route)` | untyped, `<TOutput>`, `<TInput, TOutput>` | 200 |
| `Define.Post(route)` | untyped, `<TOutput>`, `<TInput, TOutput>` | 201 |
| `Define.Put(route)` / `Define.Patch(route)` | untyped, `<TOutput>`, `<TInput, TOutput>` | 200 |
| `Define.Delete(route)` | untyped, `<TOutput>`, `<TInput, TOutput>` | 204 untyped, 200 typed (204-with-body is invalid HTTP) |
| `Define.File(route)` | untyped, `<TInput>` | 200, GET, `application/octet-stream` |

An untyped definition can become input-only via `.Accepts<TInput>()` (e.g. a PUT
that takes a body and returns 204).

## Builder methods

All return the definition for chaining.

| Method | Effect |
|---|---|
| `.Summary(text)` / `.Description(text)` | OpenAPI `summary` / `description` |
| `.Status(code)` | Override the success status. May only be called once. |
| `.Returns<T>(status[, description])` | Declare an additional typed response (errors, alternates). Each status may be declared once. |
| `.Returns(status[, description])` | Same, without a payload type. |
| `.WithResponseHeader(status, name[, description][, required:])` | Declare a response header on a status (`responses[status].headers`). String-typed; `required` is an explicit opt-in promise. **Spec-only** — Rivet never sets or validates it; emitting the header is handler code. Each (status, name) pair may be declared once. |
| `.WithResponseHeader(name[, description][, required:])` | Same, targeting the endpoint's success status. |
| `.Secure(scheme)` | Reference a security scheme by name (define it with `--security`). |
| `.Anonymous()` | No auth required (`security: []`). |
| `.QueryAuth(name = "token")` | Auth token as a required query parameter — for media players that cannot set headers. Emits `x-rivet-query-auth`. |
| `.FormEncoded()` | Request body is `application/x-www-form-urlencoded`. |
| `.AcceptsFile()` | Request body is `multipart/form-data` with a binary file part. |
| `.ProducesFile(contentType = "application/octet-stream")` | Response is a binary download. |
| `.ContentType(mediaType)` | `FileRouteDefinition` alias for `ProducesFile`. |
| `.RequestExampleJson(json, ...)` / `.ResponseExampleJson(status, json, ...)` | Attach examples. **Runtime no-ops** — read by Roslyn only. The `...Ref` variants reference component examples. |
| `.SkipValidation()` | Disable typed-result validation for framework results without a status code (`ChallengeHttpResult`, `SignOutHttpResult`). |

## Invoke

- `RouteDefinition<TInput, TOutput>.Invoke(input, handler)` →
  `RivetResult<TOutput>` with the declared success status (no runtime checks —
  compile-time types only).
- Typed-results overloads (`Invoke<T1..T6>` returning ASP.NET `Results<...>`)
  validate at request time that the returned status, payload runtime type, body
  presence, and content type match the declaration, and throw
  `RivetContractViolationException` otherwise (map it to the structured envelope
  with `RivetContractViolationHandler`). See
  [Runtime Validation](/guides/runtime-validation) for the exact scope.
- `FileRouteDefinition.Invoke<TResult>(handler)` (and the `<TInput>` variant)
  validates that the success branch carries file content matching the declared
  content type and that error statuses are declared.

## Immutability

Definitions are published on first `Invoke`; after that every builder mutator
throws. Configure the definition fully in its `static readonly` initializer.
