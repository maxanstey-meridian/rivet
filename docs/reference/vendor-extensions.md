# Vendor Extensions

Rivet emits `x-rivet-*` extensions to carry C#-level facts that plain OpenAPI
cannot express. They are what make the emit → import round-trip stable; every
other consumer can ignore them.

| Extension | Where | Meaning |
|---|---|---|
| `x-rivet-contract` | operation | Contract/controller name the operation came from (drives `operationId` grouping; reused on import). |
| `x-rivet-endpoint` | operation | The endpoint (field/method) name within that contract. |
| `x-rivet-brand` | schema | This schema is a branded value object (e.g. `record Email(string Value)`); value is the brand name. |
| `x-rivet-generic` | schema | This component is a monomorphised generic (e.g. `PagedResult_MemberDto`); records the open generic and its type arguments. |
| `x-rivet-csharp-type` | property schema | The exact C# type when the JSON Schema alone is ambiguous (`DateTimeOffset` vs `DateTime`, `ulong`, `byte[]`, unrepresentable types). |
| `x-rivet-file` | schema/media type | Binary file content (uploads and downloads). |
| `x-rivet-input-type` | request body schema | Name of the C# input record behind a multipart/synthesized body. |
| `x-rivet-query-auth` | operation | Endpoint authenticates via a query parameter: `{ "parameterName": "token" }`. |
| `x-rivet-empty-record` | schema | The C# type is a record with no properties (distinguishes it from a free-form object). |

The contract JSON consumed by `--from` is an internal intermediate representation
shared with the sibling runtimes — it is not a public format; OpenAPI (with these
extensions) is Rivet's only public output.
