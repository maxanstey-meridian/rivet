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
[vendor extensions](/reference/vendor-extensions) that make the spec losslessly
re-importable.

See the [CLI Reference](/reference/cli) for the current command set.
