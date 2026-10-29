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
tokens must be bare (`{id}`): a constraint, `?`, default or catch-all is `RIV2017`, and
the param's own `type` says what the token holds.

See the [CLI Reference](/reference/cli) for the current command set.
