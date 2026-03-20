# Vendor Extensions

Rivet emits `x-rivet-*` vendor extensions in OpenAPI specs to preserve type information that would otherwise be lost during [round-trips](/guides/openapi-round-trips). These are standard OpenAPI 3.0 extensions — non-Rivet tools ignore them.

## `x-rivet-brand`

**Applies to:** Component schema

**Type:** `string`

**Purpose:** Marks a schema as a branded value object, preserving the brand name through import.

```json
{
  "Email": {
    "type": "string",
    "x-rivet-brand": "Email"
  }
}
```

**Emitter:** Added by `OpenApiEmitter.BuildSchemas` for each brand in the model. The `Brand` arm in `MapTsTypeToJsonSchema` emits a `$ref` to the component schema instead of inlining.

**Importer:** Read by `SchemaMapper.IsBrandedString()` (takes precedence over format-based detection) and `SchemaMapper.MapBrand()` (uses the extension value as the brand name).

**Fallback:** Without this extension, the importer detects brands by `format` (`email`, `uri`, etc.) and uses the schema key as the name.

## `x-rivet-input-type`

**Applies to:** Multipart form-data schema (inside `requestBody.content.multipart/form-data.schema`)

**Type:** `string`

**Purpose:** Preserves the original C# record name for file upload input types.

```json
{
  "schema": {
    "type": "object",
    "x-rivet-input-type": "UploadInput",
    "properties": { ... }
  }
}
```

**Emitter:** Added by `OpenApiEmitter.BuildOperation` when `ep.InputTypeName` is set. `ContractWalker.BuildParams` populates `InputTypeName` from `tInput.Name` when the input record contains `IFormFile` properties.

**Importer:** Read by `ContractBuilder.ResolveInputType()` — used as the context name for `SchemaMapper.ResolveCSharpType()` instead of the default `{fieldName}Request`.

**Fallback:** Without this extension, the importer synthesizes a name like `UploadRequest` from the operation's field name.

## `x-rivet-file`

**Applies to:** Property schema (inside a multipart form-data object)

**Type:** `boolean`

**Purpose:** Marks a property as a file upload field, as a secondary signal alongside `format: binary`.

```json
{
  "document": {
    "type": "string",
    "format": "binary",
    "x-rivet-file": true
  }
}
```

**Emitter:** Added by `OpenApiEmitter.BuildOperation` on each `ParamSource.File` property.

**Importer:** Read by `SchemaMapper.ResolveStringType()` as a fallback when `format: binary` is absent.

**Fallback:** Without this extension, file detection relies solely on `format: binary`.

## `x-rivet-generic`

**Applies to:** Monomorphised component schema

**Type:** `object` with keys `name`, `typeParams`, `args`

**Purpose:** Allows the importer to reconstruct a generic template from monomorphised schemas.

```json
{
  "PagedResult_TaskDto": {
    "type": "object",
    "properties": { ... },
    "x-rivet-generic": {
      "name": "PagedResult",
      "typeParams": ["T"],
      "args": { "T": "TaskDto" }
    }
  }
}
```

| Key | Type | Description |
|---|---|---|
| `name` | `string` | Generic template name (e.g., `PagedResult`) |
| `typeParams` | `string[]` | Type parameter names in declaration order (e.g., `["T"]`) |
| `args` | `object` | Map of type parameter → concrete C# type string (e.g., `{ "T": "TaskDto" }`) |

**Emitter:** Added by `OpenApiEmitter.BuildSchemas` for each monomorphised generic instance. Type arguments are encoded as C# type strings (`"TaskDto"`, `"string"`, `"List<TaskDto>"`).

**Importer:** Read by `SchemaMapper.MapSchemas()` in a pre-scan phase:

1. Groups schemas by `x-rivet-generic.name`
2. Builds one `GeneratedRecord` per template with type parameters
3. Derives template properties by reverse-substituting concrete types back to type parameters
4. `ResolveCSharpTypeCore` resolves `$ref`s to monomorphised schemas as generic type strings (e.g., `PagedResult<TaskDto>`)

**Fallback:** Without this extension, each monomorphised schema imports as a separate concrete record (e.g., `PagedResultTaskDto`).
