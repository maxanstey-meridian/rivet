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

## Route parameters

Every `{token}` in a route is a path parameter, and the spec has to say what type it
is. Rivet takes the answer from the first of these it finds:

1. **A declared parameter**, `.Parameter<T>("id", "path", required: true)`. It replaces
   whatever `TInput` would have said about the token. Its name has to be the token's,
   exactly: `.Parameter<int>("Id", ...)` on `{id}` is `RIV1123`, since an OpenAPI path
   parameter is matched to its token by name. The importer writes these; you rarely
   need to.
2. **A property on `TInput`** whose name matches the token, ignoring case, `_`, and `-`
   (`{thing_id}` finds `ThingId`). The parameter gets the property's type and its
   annotations (see below).
3. **A type constraint** in the route: `{id:guid}`, `{page:int}`.

Whichever of the first two types the token, its constraints are checked against it
(below), so a declaration doesn't get to wave a contradiction through.

A token none of those declare is `RIV1019`, and generation stops. The only type left
to guess is a plain string, and that's usually a fib: the handler binds a `Guid`, and
the spec would tell every client any string will do. Untyped verbs count too:
`Define.Post("/forms/{id}/archive")` has no input, so its `{id}` needs a constraint.

Constraints are read by ASP.NET routing's own template parser, stacked, optional, and
with defaults (`{id:int:min(1)}`, `{id:int?}`, `{page:int=1}`). A template routing would
refuse at startup (`{a}{b}`, `{id?=5}`, an unclosed `{`) stops generation with `RIV1125`
instead of reaching the spec. So does one routing is perfectly happy with: a doubled brace
outside a token (<span v-pre>`/literal/{{x}}`</span>) is routing's escape for a literal `{`, but an OpenAPI path
has no escape for a brace at all, so there's no path to write. A constraint argument's
doubled braces (<span v-pre>`regex(^\d{{4}}$)`</span>) belong to its pattern and are fine.

One refusal gets a pass, for now. Routing won't start with a name used twice
(`/accounts/{id}/maps/{id}`), and neither will Rivet on a controller action or a
`[RivetEndpoint]` method. A contract, though, is often what `--from-openapi` wrote, and
some published specs repeat a parameter (Cloudflare's paths do). Whether import should
refuse such a path or rename the repeat is still an open decision, so until it's made, a
contract's template reads each repeat as its own token and the walk goes on.

On a controller, MVC replaces the route's `[controller]`, `[action]`, and `[area]` tokens
before routing ever sees it, and Rivet calls MVC's own `ReplaceTokens` to do the same:
the controller's name less its `Controller` suffix, the method's name, and the name an
`[Area]` gives. A token MVC has no value for (`[area]` with no `[Area]`, or a typo like
`[controler]`) or an unclosed `[` stops MVC at startup, so it's `RIV1125` here too. That's
also why an attribute route spells a regex class with doubled brackets,
<span v-pre>`regex(^[[a-z]]{{2}}$)`</span>: a single `[` starts a token, and the replacement unescapes them
before the constraint is read.

| Constraint | Path parameter schema |
|---|---|
| `int`, `long` | `integer`, `int32` / `int64` |
| `guid` | `string`, `uuid` |
| `bool` | `boolean` |
| `datetime` | `string`, `date-time` |
| `decimal`, `double`, `float` | `number` with that format |
| `min(n)`, `max(n)`, `range(a,b)` | `minimum` / `maximum`; integers only, as routing parses them |
| `length(n)`, `length(a,b)`, `minlength(n)`, `maxlength(n)` | `minLength` / `maxLength` |
| `alpha` | `pattern: ^[a-zA-Z]*$` |
| `regex(p)` | `pattern`, with each letter matching either case, because routing ignores case and a JSON Schema pattern can't |

The `?` and the default are routing's business: an OpenAPI path parameter is always
required. `required` adds nothing for the same reason. `file` and `nonfile` say whether
the segment looks like a file name, which no schema keyword can, so they add nothing
either, and a custom constraint you register in code is invisible to Rivet.

A token with a property *and* a constraint gets both, and they have to agree.
`{id:int}` over a `string Id` is `RIV1125`: routing lets only integers through while
the binder reads a string, and the spec can only tell one of those stories. The same
goes for a keyword the type can't carry (`min(1)` on a string, `length(3)` on an int),
two type constraints (`{id:int:guid}`), and two patterns, since a schema holds one.
Bounds that can both hold are intersected: `[Range(1, 100)]` on the property and
`{page:int:range(5,500)}` in the route make `5` to `100`, which is exactly what gets
past routing and validation both. `[RivetEndpoint]` actions follow the same rules, with
the action parameter in the property's place; a token no action parameter binds is
declared by its constraint or stops generation with `RIV1019`.

### Parameters lowered from `TInput`

When `TInput` turns into route, query, and header parameters (a GET or DELETE, an
`.AcceptsBinary()` body, a `[RivetHeader]` property, or the route tokens of any verb),
each parameter keeps what the property says about itself: `[RivetDescription]`,
`[Range]`/`[StringLength]`/`[RegularExpression]` and friends, `[RivetDefault]`,
`[Obsolete]`, `[RivetFormat]` (or `[EmailAddress]`, `[Url]`), `[RivetSchemaType]`, and
`[RivetExample]`. The parameter's schema reads as the property's would in a body, so
`[Range(1, 100)] int Limit` on a GET emits `minimum: 1, maximum: 100` on `?limit=`.
`[FromQuery]`, `[FromRoute]`, and `[FromHeader]` action parameters keep theirs too.

## Builder methods

All return the definition for chaining. Rivet reads the chain from source rather than
running it, so every string, int or bool you pass (route, summary, status code, media
type, scheme, scope, `required:`) has to be a literal or a `const`. A variable stops
generation with `RIV1113`: Rivet can't tell it from an argument you left out, and
guessing the default would put the wrong value in the spec.

| Method | Effect |
|---|---|
| `.Summary(text)` / `.Description(text)` | OpenAPI `summary` / `description` |
| `.Status(code)` | Override the success status. May only be called once. |
| `.StatusKey(key[, description])` | Key the success response by a range (`"2XX"`) or `"default"` instead of one code. See [Status keys](#status-keys). |
| `.Returns<T>(status[, description])` | Declare an additional typed response (errors, alternates). Each status may be declared once. `status` is a code or a [status key](#status-keys). |
| `.Returns(status[, description])` | Same, without a payload type. |
| `.WithResponseHeader<T>(status, name[, description][, required:])` | Declare a typed response header on a status (`responses[status].headers`). The non-generic overload declares a string header. Rivet never sets it: emitting the header is handler code. `required: true` is a promise the adapters hold you to, so a response at that status written without the header throws `RivetContractViolationException` (see [Runtime responses](#runtime-responses)). Each (status, name) pair may be declared once. |
| `.WithResponseHeader<T>(name[, description][, required:])` | Same, targeting the endpoint's success status; omit `<T>` for a string header. |

| `.Secure(scheme[, scopes...])` | One way in: the named scheme, granting these scopes. Call it again for another way in; OpenAPI ORs them. Define the scheme with `--security` or `[assembly: RivetSecurityScheme(...)]`. See [Security](#security). |
| `.AndSecure(scheme[, scopes...])` | Adds a scheme to the alternative the previous `.Secure()` started: the caller needs both. |
| `.OrAnonymous()` | Adds a no-credentials alternative (`{}`), so authentication is optional. |
| `.Anonymous()` | No auth required (`security: []`). Stands alone. |
| `.Parameter<T>(name, location, required[, schemaType, format, metadataJson, schemaRef])` | Declares a `path`, `query`, `header`, or `cookie` parameter of type `T` outright, replacing any `TInput` property of that name and place. Import writes it; a `path` one declares its route token (see [Route parameters](#route-parameters)). |
| `.QueryAuth(name = "token")` | Auth token as a required query parameter — for media players that cannot set headers. Emits `x-rivet-query-auth`. |
| `.FormEncoded()` | Request body is `application/x-www-form-urlencoded`. |
| `.AcceptsFile()` | Request body is `multipart/form-data` with a binary file part. |
| `.AcceptsBinary(contentType = "application/octet-stream")` | Request body is the raw bytes (`type: string, format: binary`). **Spec-only** — host code reads the stream; `TInput` properties lower to route/query params instead of a JSON body. Mutually exclusive with `.AcceptsFile()` / `.FormEncoded()`. |
| `.ProducesFile(contentType = "application/octet-stream")` | Response is a binary download. |
| `.AcceptsContentType(mediaType)` | Declared media type for a non-JSON request body (e.g. `"text/plain"` for a `string` body). The body SCHEMA is unchanged — only the content-type key. **Spec-only.** Mutually exclusive with `.FormEncoded()` / `.AcceptsBinary()`. |
| `.ProducesContentType(mediaType)` | Declared media type for a non-JSON success response (e.g. `"text/html"`). Schema unchanged; error responses stay `application/json`. `.Success(...)` writes with it, and a non-JSON type needs a `string` payload. Mutually exclusive with `.ProducesFile()`. |
| `.RequestExampleJson(json, ...)` / `.ResponseExampleJson(status, json, ...)` | Attach examples. **Runtime no-ops** — read by Roslyn only. The `...Ref` variants reference component examples: one id is one `components.examples` entry, so every endpoint that pins it must give the same value (and, where they say one, the same summary and description), or generation stops with `RIV2015`. |

A response header can't be called `Content-Type`, in any casing. OpenAPI says a response
header by that name "SHALL be ignored", so the spec would never carry it while the
adapters held the handler to it. Generation stops with `RIV2009` and the builder throws
at startup; a response's media type is `.ProducesContentType(...)` (or the status's
`.ResponseContent(...)`). A `schemaType:` on a typed header re-types a primitive, so on a
`T` that maps to anything else (a record, a collection, an enum, a `[RivetScalar]` brand)
it's `RIV1117` rather than quietly vanishing.

## Status keys

A call that names a response takes an `int` code, or a string key in its string
overloads (`.StatusKey()`, `.Returns()`, `.ResponseContent()`, `.WithResponseHeaderKey()`,
`.ResponseExampleJson()` and the rest). The key is one of the three things an OpenAPI
response can be keyed by:

- a code, `"404"`;
- a range, `"4XX"`, meaning every 4xx the operation didn't declare on its own;
- `"default"`, meaning every status nothing else claims.

Generation checks each key by the same rule `--from` applies to contract JSON, so a typo
stops it with `RIV1120` instead of reaching the spec, where no client could match it. So
does an `int` outside 100 to 599 (and a `[ProducesResponseType]` one on a controller).
`"default"` is the one forgiving case: the runtime matches it ignoring case, on purpose,
so `"DEFAULT"` and `"Default"` work too, and the spec gets the lowercase `default` OpenAPI
actually knows. A range keeps the spelling you gave it.

The runtime holds the same line. `.StatusKey("abc")` and `.Returns("6XX")` throw an
`InvalidOperationException` naming the route the moment the definition is built, so a
key that slipped past generation (say, a project nobody regenerated) can't serve a
response keyed by nothing.

## Security

OpenAPI security is an OR of AND-groups: the caller satisfies any one alternative, and
an alternative only by satisfying every scheme in it, each with its scopes. The builder
spells that out in the order you'd say it aloud. Most endpoints only ever need the
first line:

```csharp
Define.Get<TaskDto>("/api/tasks/{id:guid}").Secure("bearer");
// security: [{ bearer: [] }]

Define.Delete("/api/tasks/{id:guid}").Secure("oauth", "tasks:write");
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

An endpoint with none of these inherits the document's security: the
`[assembly: RivetGlobalSecurity]` default (see [Attributes](attributes.md#security), which
an import writes too), or the first `--security` scheme when you pass `--security`, which
replaces the attributes. Controllers declare the same things with
[attributes](attributes.md#operations-controllers-and-actions): `[RivetSecure]`,
`[RivetAndSecure]`, `[RivetOrAnonymous]` and `[RivetAnonymous]`.

These are spec-only: Rivet writes them into the OpenAPI document and nothing at
runtime checks a token. Authorization stays with ASP.NET (`RequireAuthorization`,
`[Authorize]`). Scheme names and scopes follow the literal-or-`const` rule above
(`RIV1113`), and the calls have to agree with each other:
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
  set it before the endpoint runs instead. That's deliberate. Rivet can't simply check
  in an `OnStarting` callback of its own, because callbacks run last-registered-first
  and the handler registers its callback after the middleware has, so Rivet's would
  run first and still find no header. Only a new middleware of Rivet's that every host
  registered ahead of everything else (a hypothetical `UseRivet()`; there's no such
  thing today) would run last, and that's a pipeline requirement to buy a header style
  that's rare in practice.

See [Runtime Validation](/guides/runtime-validation) for the exact enforcement scope
and `RivetContractViolationHandler` for the structured failure envelope.

## Immutability

Definitions are published on the first `Bind`, `Success`, `Error`, or `File`; after
that every builder mutator throws. Configure the definition fully in its
`static readonly` initializer.
