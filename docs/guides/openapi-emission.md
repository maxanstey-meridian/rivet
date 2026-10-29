# OpenAPI Emission

OpenAPI 3.1 is Rivet's output. `--output <dir>` writes `<dir>/openapi.json`:

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
```

Optional spec metadata and security (`info.title` defaults to `API`, `info.version`
to `1.0.0`; without `--server` no `servers` block is emitted):

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated \
  --title "Orders API" --version 2.3.0 --server https://api.example.com \
  --security bearer
```

Explicit spec path (relative paths resolve against `--output`):

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi ../spec/openapi.json
```

What flows into the spec: component schemas for every walked type (brands, monomorphised
generics, enums, nullability as 3.1 type arrays, validation constraints, descriptions and
examples), operations for every contract/controller endpoint (params classified as
route/query/body/form/file, per-status typed responses, multipart and form-encoded
bodies, file endpoints with `x-rivet-file`), security schemes, and the `x-rivet-*`
[vendor extensions](/reference/vendor-extensions) that preserve supported Rivet
metadata across re-import. Shapes outside the importer profile degrade with
diagnostics or structured unsupported markers rather than being presented as lossless.

A component example is one entry in `components.examples`, however many operations
point at it. Endpoints that pin the same id with the same value share it, and the entry
carries the summary and description any of them gives. Two that pin it with different
values, or with summaries (or descriptions) that disagree, stop generation with
`RIV2015`: the alternative is the second `$ref` quietly showing the first one's example.

Contract JSON (`--from`) can say what the whole document requires with a top-level
`security`, the same shape an endpoint's `security` takes and the counterpart of the
C# side's `[assembly: RivetGlobalSecurity*]`. It becomes the root `security` and wins
over the default `--security` would otherwise supply; `--security` still defines the
schemes, since contract JSON carries no scheme definitions. `{ "alternatives": [] }` is
`security: []`. A `routeTemplate` is written as the path key exactly as given, so its
tokens must be bare (`{id}`): a constraint, `?`, default, or catch-all is `RIV2017`, and
the param's own `type` says what the token holds.

Every response key is one OpenAPI can hold, and one rule decides which: the runtime's. A
key is a status from 100 to 599, a range like `4XX`, or `default`. Generation stops on
anything else with `RIV1120`, contract JSON with `RIV2017` (its `statusCode` and
`statusKey` both), and the runtime turns the same key away when the definition is built,
on every builder call that takes a status, so a `.WithResponseHeaderKey("abc", ...)` can't
quietly enforce nothing.

A form body never says anything about fields it doesn't list. ASP.NET's form binders
neither fill a `[JsonExtensionData]` bag nor turn away a field they don't bind, so an input
record that's open (or closed) as JSON is neither as a form. Its body is spelled out
inline, without `additionalProperties`, rather than pointing at the record's component.
The body still pins a record name for import, and it has to be one no component holds,
because the importer gives every component its own name first and would quietly suffix
anything that collides. So the record's own name loses to its component, the importer's
`{Name}Request` stands in, and when that's taken too, as it is when `addComment`'s input
is the house-style `AddCommentRequest`, the pin moves on to `AddCommentRequest_2`. That's
the name the importer lands on, so the pin and the import agree.

See the [CLI Reference](/reference/cli) for the current command set.
