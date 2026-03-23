# Vendor Extensions

Rivet emits `x-rivet-*` vendor extensions in OpenAPI specs to preserve type information that would otherwise be lost
during [round-trips](/guides/openapi-round-trips). These are standard OpenAPI 3.0 extensions — non-Rivet tools ignore
them.

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

**Emitter:** Added by `OpenApiEmitter.BuildSchemas` for each brand in the model. The `Brand` arm in
`MapTsTypeToJsonSchema` emits a `$ref` to the component schema instead of inlining.

**Importer:** Read by `SchemaMapper.IsBrandedString()` (takes precedence over format-based detection) and
`SchemaMapper.MapBrand()` (uses the extension value as the brand name).

**Fallback:** Without this extension, the importer detects brands by `format` (`email`, `uri`, etc.) and uses the schema
key as the name.

## `x-rivet-input-type`

**Applies to:** Multipart form-data schema (inside `requestBody.content.multipart/form-data.schema`)

**Type:** `string`

**Purpose:** Preserves the original C# record name for file upload input types. Used by the importer as a fallback when the multipart schema is inlined rather than using `$ref`.

**Current emitter behaviour:** When the input type has a name (i.e., it's a defined record), the emitter uses `$ref` to the component schema instead of inlining. The `x-rivet-input-type` extension is no longer emitted by the current emitter, but the importer still reads it for backwards compatibility with older specs.

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

**Importer:** Read by `ContractBuilder.ResolveInputType()` — used as the context name for
`SchemaMapper.ResolveCSharpType()` instead of the default `{fieldName}Request`. When the schema is a `$ref`, the type name comes from the ref path directly.

**Fallback:** Without this extension (and without a `$ref`), the importer synthesizes a name like `UploadRequest` from the operation's field name.

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

| Key          | Type       | Description                                                                  |
|--------------|------------|------------------------------------------------------------------------------|
| `name`       | `string`   | Generic template name (e.g., `PagedResult`)                                  |
| `typeParams` | `string[]` | Type parameter names in declaration order (e.g., `["T"]`)                    |
| `args`       | `object`   | Map of type parameter → concrete C# type string (e.g., `{ "T": "TaskDto" }`) |

**Emitter:** Added by `OpenApiEmitter.BuildSchemas` for each monomorphised generic instance. Type arguments are encoded
as C# type strings (`"TaskDto"`, `"string"`, `"List<TaskDto>"`).

**Importer:** Read by `SchemaMapper.MapSchemas()` in a pre-scan phase:

1. Groups schemas by `x-rivet-generic.name`
2. Builds one `GeneratedRecord` per template with type parameters
3. Derives template properties by reverse-substituting concrete types back to type parameters
4. `ResolveCSharpTypeCore` resolves `$ref`s to monomorphised schemas as generic type strings (e.g.,
   `PagedResult<TaskDto>`)

**Fallback:** Without this extension, each monomorphised schema imports as a separate concrete record (e.g.,
`PagedResultTaskDto`).

## `x-rivet-csharp-type`

**Applies to:** Property schema (any primitive type)

**Type:** `string`

**Purpose:** Preserves the exact C# type when `type`+`format` alone would be ambiguous. Only emitted for types where the
default format-based import would lose information.

```json
{
  "flags": {
    "type": "integer",
    "format": "int32",
    "x-rivet-csharp-type": "uint"
  },
  "createdAt": {
    "type": "string",
    "format": "date-time",
    "x-rivet-csharp-type": "DateTimeOffset"
  }
}
```

**Types that use this extension:**

| C# type          | OpenAPI type + format | Why needed                 |
|------------------|-----------------------|----------------------------|
| `DateTimeOffset` | `string, date-time`   | Same format as `DateTime`  |
| `uint`           | `integer, int32`      | Same format as `int`       |
| `ulong`          | `integer, int64`      | Same format as `long`      |
| `short`          | `integer, int16`      | No standard format mapping |
| `ushort`         | `integer, uint16`     | No standard format mapping |
| `byte`           | `integer, uint8`      | No standard format mapping |
| `sbyte`          | `integer, int8`       | No standard format mapping |

**Emitter:** Added by `OpenApiEmitter.MapPrimitive` when `TsType.Primitive.CSharpType` is set. The `TypeWalker` sets
`CSharpType` only for types that can't be recovered from `Name`+`Format`.

**Importer:** Read by `SchemaMapper.ResolveSingleType()` — checked before format-based resolution. If present, the
extension value is used as the C# type directly.

**Fallback:** Without this extension (e.g. third-party specs), the importer uses format-based defaults: `int32` → `int`,
`int64` → `long`, bare `integer` (no format) → `long`, `date-time` → `DateTime`, `number` → `double`.
