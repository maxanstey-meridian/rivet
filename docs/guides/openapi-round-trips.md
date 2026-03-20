# OpenAPI Round-Trips

When you [emit](/guides/openapi-emission) an OpenAPI spec from your C# contracts and later [import](/guides/openapi-import) it back, certain type information would normally be lost — the OpenAPI spec doesn't natively represent branded types, generic templates, or the original record name for file upload inputs.

Rivet solves this with `x-rivet-*` vendor extensions. These are standard OpenAPI 3.0 extensions — non-Rivet consumers ignore them, the spec stays valid.

## What survives a round-trip

| C# construct | Without extensions | With extensions |
|---|---|---|
| `Email(string Value)` (branded VO) | Collapses to `string` | Preserved as `Email(string Value)` |
| `PagedResult<TaskDto>` (generic) | Flat `PagedResult_TaskDto` record | Reconstructed as `PagedResult<T>` template |
| `UploadInput(IFormFile Doc, string Title)` | Anonymous `UploadRequest` record | Named `UploadInput` record |
| Enums, nullable types, arrays, dicts | Already preserved | Already preserved |

## How it works

### Brands

A branded value object like `Email(string Value)` emits as a component schema with `x-rivet-brand`:

```json
{
  "Email": {
    "type": "string",
    "x-rivet-brand": "Email"
  }
}
```

Properties that reference it use `$ref`:

```json
{ "email": { "$ref": "#/components/schemas/Email" } }
```

On import, the extension tells the importer to generate a branded record (`Email(string Value)`) instead of treating it as a plain `string` alias.

### Generics

`PagedResult<TaskDto>` emits as a monomorphised schema `PagedResult_TaskDto` with `x-rivet-generic`:

```json
{
  "PagedResult_TaskDto": {
    "type": "object",
    "properties": {
      "items": { "type": "array", "items": { "$ref": "#/components/schemas/TaskDto" } },
      "totalCount": { "type": "number" }
    },
    "x-rivet-generic": {
      "name": "PagedResult",
      "typeParams": ["T"],
      "args": { "T": "TaskDto" }
    }
  }
}
```

On import, the importer:

1. Groups all schemas with the same `x-rivet-generic.name`
2. Emits one generic record per template (`PagedResult<T>`)
3. Resolves `$ref`s to monomorphised schemas as generic type strings (`PagedResult<TaskDto>`)

Multiple instantiations (`PagedResult<TaskDto>`, `PagedResult<UserDto>`) produce one shared template.

### File uploads

A multipart endpoint with `UploadInput(IFormFile Doc, string Title)` emits:

```json
{
  "schema": {
    "type": "object",
    "x-rivet-input-type": "UploadInput",
    "properties": {
      "doc": { "type": "string", "format": "binary", "x-rivet-file": true },
      "title": { "type": "string" }
    }
  }
}
```

- `x-rivet-input-type` preserves the record name (without it, the importer would synthesize `UploadRequest`)
- `x-rivet-file` marks file properties as a fallback signal alongside `format: binary`

## Double round-trips

The extensions are idempotent. C# → OpenAPI → C# → OpenAPI → C# produces the same types as a single round-trip. This is tested in `OpenApiRoundTripTests.Double_RoundTrip_Is_Stable`.

## What doesn't survive

- **Numeric format distinctions** — `int`, `long`, `double` all emit as `type: number`. On import, `integer` maps back to `int` and `number` to `double`, but `decimal` and `uint` are not distinguished.
- **Date format distinctions** — `DateTime`, `DateTimeOffset`, `DateOnly` all emit as `type: string`. On import, `format: date-time` maps to `DateTime`.
- **Non-format brands** — Only brands emitted by Rivet (which carry `x-rivet-brand`) survive. A third-party spec with `format: email` will still import as a brand, but the name comes from the schema key, not an extension.

## Non-Rivet consumers

The extensions are invisible to tools that don't look for them. Swagger UI, Redocly, API gateways, and other OpenAPI consumers render the spec normally. The `x-` prefix is the standard mechanism for vendor extensions in OpenAPI 3.0.

## Extension reference

See [Vendor Extensions](/reference/vendor-extensions) for the full specification of each extension.
