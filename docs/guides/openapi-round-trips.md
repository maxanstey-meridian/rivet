# OpenAPI Round-Trips

When you [emit](/guides/openapi-emission) an OpenAPI spec from your C# contracts and
later [import](/guides/openapi-import) it back, certain type information would normally be lost — the OpenAPI spec
doesn't natively represent branded types, generic templates, or the original record name for file upload inputs.

Rivet solves this with `x-rivet-*` vendor extensions. These are standard OpenAPI 3.0 extensions — non-Rivet consumers
ignore them, the spec stays valid.

## What survives a round-trip

| C# construct                               | Without extensions                | With extensions                            |
|--------------------------------------------|-----------------------------------|--------------------------------------------|
| `Email(string Value)` (branded VO)         | Collapses to `string`             | Preserved as `Email(string Value)`         |
| `PagedResult<TaskDto>` (generic)           | Flat `PagedResult_TaskDto` record | Reconstructed as `PagedResult<T>` template |
| `UploadInput(IFormFile Doc, string Title)` | Anonymous `UploadRequest` record  | Named `UploadInput` record (via `$ref`)    |
| `DateTimeOffset`, `uint`, `short`, etc.    | Falls back to `DateTime`/`int`    | Preserved via `x-rivet-csharp-type`        |
| Enums, nullable types, arrays, dicts       | Already preserved                 | Already preserved                          |
| Property metadata (description, etc.)      | Lost                              | Preserved via `[Rivet*]` attributes        |
| Enum member names (`in-progress`)          | PascalCased (`InProgress`)        | Preserved via `[JsonStringEnumMemberName]` |
| Form-encoded bodies                        | Converted to JSON                 | Preserved via `.FormEncoded()`             |
| Void error responses (404 no body)         | Dropped                           | Preserved via `.Returns(statusCode)`       |

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

On import, the extension tells the importer to generate a branded record (`Email(string Value)`) instead of treating it
as a plain `string` alias.

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

A multipart endpoint with `UploadInput(IFormFile Doc, string Title)` emits a `$ref` to the component schema:

```json
{
  "requestBody": {
    "content": {
      "multipart/form-data": {
        "schema": { "$ref": "#/components/schemas/UploadInput" }
      }
    }
  }
}
```

The component schema has `x-rivet-file` on file properties:

```json
{
  "UploadInput": {
    "type": "object",
    "properties": {
      "doc": { "type": "string", "format": "binary", "x-rivet-file": true },
      "title": { "type": "string" }
    }
  }
}
```

- The `$ref` preserves the record name directly (the importer reads it from the ref path)
- `x-rivet-file` marks file properties as a fallback signal alongside `format: binary`

## Double round-trips

The extensions are idempotent. C# → OpenAPI → C# → OpenAPI → C# produces the same types as a single round-trip. This is
tested in `OpenApiRoundTripTests.Double_RoundTrip_Is_Stable`.

## What also survives

- **Property metadata** — `description`, `default`, `example`, `readOnly`, `writeOnly`, and validation constraints (`minLength`, `maxLength`, `pattern`, `minimum`, `maximum`, etc.) are preserved through `[RivetDescription]`, `[RivetDefault]`, `[RivetExample]`, `[RivetReadOnly]`, `[RivetWriteOnly]`, `[RivetConstraints]`, and `[RivetFormat]` attributes
- **Type descriptions** — schema-level `description` is preserved via `[RivetDescription]` on the record
- **Enum member names** — non-PascalCase enum values (e.g., `in-progress`, `my_status`) are preserved via `[JsonStringEnumMemberName]` attributes
- **Summary / Description** — OpenAPI `summary` and `description` are preserved as separate `.Summary()` and `.Description()` builder calls
- **Form-encoded bodies** — `application/x-www-form-urlencoded` content type is preserved via `.FormEncoded()`
- **Void error responses** — error responses without a body (e.g., 404 with no content) are preserved via `.Returns(statusCode)`

## What doesn't survive

- **Non-format brands** — Only brands emitted by Rivet (which carry `x-rivet-brand`) survive. A third-party spec with
  `format: email` will still import as a brand, but the name comes from the schema key, not an extension.

All primitive C# types now survive round-trips, including `DateTimeOffset`, `uint`, `ulong`, `short`, `byte`, and `sbyte`.
These use `x-rivet-csharp-type` to preserve the exact type when `type`+`format` alone would be ambiguous.
Without the extension (e.g. third-party specs), the importer uses `format`-based defaults: `date-time` → `DateTime`,
`int32` → `int`, bare `integer` → `long`, `int64` → `long`, `number` → `double`.

## Non-Rivet consumers

The extensions are invisible to tools that don't look for them. Swagger UI, Redocly, API gateways, and other OpenAPI
consumers render the spec normally. The `x-` prefix is the standard mechanism for vendor extensions in OpenAPI 3.0.

## Extension reference

See [Vendor Extensions](/reference/vendor-extensions) for the full specification of each extension.
