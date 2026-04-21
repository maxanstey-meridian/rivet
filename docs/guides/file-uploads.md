# File Uploads

`IFormFile` parameters are detected automatically. The generated client constructs `FormData` and lets the browser set the correct `Content-Type` with multipart boundary.

## Example

```csharp
[RivetEndpoint]
[HttpPost("{id:guid}/attachments")]
[ProducesResponseType(typeof(AttachmentResultDto), StatusCodes.Status201Created)]
public async Task<IActionResult> Attach(Guid id, IFormFile file, CancellationToken ct)
{
    return StatusCode(StatusCodes.Status201Created,
        new AttachmentResultDto(Guid.NewGuid(), file.FileName, file.Length));
}
```

```typescript
// Generated — transport-shaped input, FormData construction handled automatically
export function attach(input: { params: { id: string; }; body: { file: File; }; }): Promise<AttachmentResultDto>;
export function attach(input: { params: { id: string; }; body: { file: File; }; }, opts: { unwrap: false }): Promise<RivetResult<AttachmentResultDto>>;
export async function attach(input: { params: { id: string; }; body: { file: File; }; }, opts?: { unwrap?: boolean }) {
  const fd = new FormData();
  fd.append("file", input.body.file);
  return rivetFetch("POST", `/api/tasks/${input.params.id}/attachments`, { body: fd, unwrap: opts?.unwrap });
}
```

## `IFormFile` in contract records

`IFormFile` is a mapped scalar type in Rivet. You can use it as a property in `[RivetType]` records for multipart upload contracts:

```csharp
[RivetType]
public sealed record UploadInput(IFormFile Document, string Title);

[RivetContract]
public static class FilesContract
{
    public static readonly RouteDefinition<UploadInput, UploadResult> Upload =
        Define.Post<UploadInput, UploadResult>("/api/files")
            .AcceptsFile();
}
```

The emitter produces a `multipart/form-data` request body with `$ref` to the component schema. File properties are marked with `format: binary` and `x-rivet-file: true`.

## Limitations

- **Single file only** — `IFormFileCollection` and `List<IFormFile>` are not supported
- The parameter must be typed as `IFormFile`
