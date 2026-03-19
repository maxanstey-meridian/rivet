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
// Generated — File parameter, FormData construction handled automatically
export function attach(id: string, file: File): Promise<AttachmentResultDto>;
export function attach(id: string, file: File, opts: { unwrap: false }): Promise<RivetResult<AttachmentResultDto>>;
export async function attach(id: string, file: File, opts?: { unwrap?: boolean }) {
  const fd = new FormData();
  fd.append("file", file);
  return rivetFetch("POST", `/api/tasks/${id}/attachments`, { body: fd, unwrap: opts?.unwrap });
}
```

## Limitations

- **Single file only** — `IFormFileCollection` and `List<IFormFile>` are not supported
- The parameter must be typed as `IFormFile`
