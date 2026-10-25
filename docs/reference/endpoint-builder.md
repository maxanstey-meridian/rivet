# Route Definition API

Contract endpoints are built with the `Define` factory and a fluent builder. Roslyn
reads the chain at generation time; the same object binds transport input and
constructs contract-owned responses at runtime.

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
| `.WithResponseHeader<T>(status, name[, description][, required:])` | Declare a typed response header on a status (`responses[status].headers`). The non-generic overload declares a string header. Rivet never sets it: emitting the header is handler code. `required: true` is a promise the adapters hold you to, so a response at that status written without the header throws `RivetContractViolationException` (see [Runtime responses](#runtime-responses)). Each (status, name) pair may be declared once. |
| `.WithResponseHeader<T>(name[, description][, required:])` | Same, targeting the endpoint's success status; omit `<T>` for a string header. |
| `.Secure(scheme[, scopes...])` | One way in: the named scheme, granting these scopes. Call it again for another way in; OpenAPI ORs them. Define the scheme with `--security`. See [Security](#security). |
| `.AndSecure(scheme[, scopes...])` | Adds a scheme to the alternative the previous `.Secure()` started: the caller needs both. |
| `.OrAnonymous()` | Adds a no-credentials alternative (`{}`), so authentication is optional. |
| `.Anonymous()` | No auth required (`security: []`). Stands alone. |
| `.QueryAuth(name = "token")` | Auth token as a required query parameter — for media players that cannot set headers. Emits `x-rivet-query-auth`. |
| `.FormEncoded()` | Request body is `application/x-www-form-urlencoded`. |
| `.AcceptsFile()` | Request body is `multipart/form-data` with a binary file part. |
| `.AcceptsBinary(contentType = "application/octet-stream")` | Request body is the raw bytes (`type: string, format: binary`). **Spec-only** — host code reads the stream; `TInput` properties lower to route/query params instead of a JSON body. Mutually exclusive with `.AcceptsFile()` / `.FormEncoded()`. |
| `.ProducesFile(contentType = "application/octet-stream")` | Response is a binary download. |
| `.AcceptsContentType(mediaType)` | Declared media type for a non-JSON request body (e.g. `"text/plain"` for a `string` body). The body SCHEMA is unchanged — only the content-type key. **Spec-only.** Mutually exclusive with `.FormEncoded()` / `.AcceptsBinary()`. |
| `.ProducesContentType(mediaType)` | Declared media type for a non-JSON success response (e.g. `"text/html"`). Schema unchanged; error responses stay `application/json`. `.Success(...)` writes with it, and a non-JSON type needs a `string` payload. Mutually exclusive with `.ProducesFile()`. |
| `.RequestExampleJson(json, ...)` / `.ResponseExampleJson(status, json, ...)` | Attach examples. **Runtime no-ops** — read by Roslyn only. The `...Ref` variants reference component examples. |

## Security

OpenAPI security is an OR of AND-groups: the caller satisfies any one alternative, and
an alternative only by satisfying every scheme in it, each with its scopes. The builder
spells that out in the order you'd say it aloud. Most endpoints only ever need the
first line:

```csharp
Define.Get<TaskDto>("/api/tasks/{id}").Secure("bearer");
// security: [{ bearer: [] }]

Define.Delete("/api/tasks/{id}").Secure("oauth", "tasks:write");
// security: [{ oauth: ["tasks:write"] }]

Define.Post("/api/reports")
    .Secure("oauth", "reports:write")           // either an OAuth token with that scope...
    .Secure("apiKey").AndSecure("signature");   // ...or an API key plus a request signature
// security: [{ oauth: ["reports:write"] }, { apiKey: [], signature: [] }]

Define.Get<FeedDto>("/api/feed").Secure("bearer").OrAnonymous();
// security: [{ bearer: [] }, {}]: signed-in callers get more, everyone gets something

Define.Get("/api/health").Anonymous();
// security: []
```

An endpoint with none of these inherits the document's security: the first
`--security` scheme, or whatever an import recorded at document level.

These are spec-only: Rivet writes them into the OpenAPI document and nothing at
runtime checks a token. Authorization stays with ASP.NET (`RequireAuthorization`,
`[Authorize]`). Rivet reads the calls from source, so scheme names and scopes must
be literals or `const`s (`RIV1113`), and the calls have to agree with each other:
`.Anonymous()` stands alone, and `.AndSecure()` joins the alternative a `.Secure()`
started (`RIV1114`).

## Runtime responses

- Input-bearing definitions use `.Bind(input)` first. It accepts the declared
  `TInput` and returns a bound endpoint retaining the declared output contract.
  Definitions without input use the terminal methods directly.
- `.Success(payload)` constructs the declared success response; void definitions
  use `.Success()`. The payload must match the declared `TOutput`.
- `.Error(status, payload)` selects a typed response declared with `.Returns<T>()`;
  `.Error(status)` selects a declared bodyless response. Undeclared statuses and
  payload mismatches throw `RivetContractViolationException`.
- `.File(content, ..., contentType: "image/jpeg")` selects a declared binary
  representation. Omit `contentType` only when the success representation is
  unambiguous.
- `.File(content, ...)` constructs a declared binary response from `byte[]`,
  `Stream`, or an absolute physical path. It carries the contract content type and
  optional download name, range processing, last-modified value, and entity tag.
- `.File(writer, ...)` takes a `Func<Stream, Task>` instead, for a body that only
  exists while it's being sent: the writer gets the response stream and fills it.
  Same contract checks, run before the writer starts, and the same headers. There's
  no `enableRangeProcessing`, because bytes written as they go have no ranges to
  serve, so a player that wants to seek asks for a new stream (say, a start time in
  the query) rather than a `Range`. The writer runs after the handler returns, so
  open what it needs inside it, and write asynchronously: Kestrel refuses synchronous
  writes. For cancellation, close over `HttpContext.RequestAborted`:

  ```csharp
  app.MapGet(MediaContract.Live.Route, (Guid id, HttpContext http) =>
      MediaContract.Live
          .File(
              output => transcoder.RunAsync(id, output, http.RequestAborted),
              contentType: "video/mp4"
          )
          .ToResult());
  ```
- All terminals return `RivetResult`. Use the first-party `.ToActionResult()` for
  MVC or `.ToResult()` for minimal APIs at the host boundary. Application execution
  remains ordinary C# outside the contract response API.
- A header declared `required: true` has to be on the response when the adapter
  runs. Set it on `HttpContext.Response.Headers` before returning the result, the
  way a redirect sets `Location`:

  ```csharp
  Response.Headers.Location = uri.ToString();
  return DownloadsContract.LatestWindows.Success().ToActionResult();
  ```

  A file gets the headers its own arguments produce for free: `entityTag:` gives an
  `ETag`, `lastModified:` a `Last-Modified`, `downloadName:` or `inline:` a
  `Content-Disposition`, `enableRangeProcessing:` an `Accept-Ranges`, and a body whose
  length is known (bytes, a path, a seekable stream) a `Content-Length`. A header a
  middleware only adds in `OnStarting` turns up after the check, so it doesn't count:
  set it before the endpoint runs instead.

See [Runtime Validation](/guides/runtime-validation) for the exact enforcement scope
and `RivetContractViolationHandler` for the structured failure envelope.

## Immutability

Definitions are published on the first `Bind`, `Success`, `Error`, or `File`; after
that every builder mutator throws. Configure the definition fully in its
`static readonly` initializer.
