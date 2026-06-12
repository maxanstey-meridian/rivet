# File Uploads & Downloads

## Uploads (multipart/form-data)

On controller endpoints, an `IFormFile` parameter — or a collection of them
(`List<IFormFile>`, `IFormFile[]`, `IReadOnlyList<IFormFile>`, ...) — makes the
operation a `multipart/form-data` request body; a single file property is emitted as
`{ "type": "string", "format": "binary" }`, a collection as an array of binary parts,
and other parameters are classified alongside it (route params stay in the path,
scalars join the form body).

On contracts, a `TInput` that is `IFormFile` itself, or a record with `IFormFile`
(or collection-of-`IFormFile`) properties, is detected as multipart automatically.
`.AcceptsFile()` marks a definition explicitly:

```csharp
public static readonly RouteDefinition<UploadRequest, UploadResponse> Upload =
    Define.Post<UploadRequest, UploadResponse>("/api/files")
        .AcceptsFile();
```

## Raw binary request bodies

`.AcceptsBinary(contentType = "application/octet-stream")` declares that the request
body is the raw bytes — no multipart envelope, no JSON schema. The canonical case is
a chunked upload, where each chunk is PUT as an opaque byte stream:

```csharp
[RivetType]
public sealed record UploadChunkInput(string Id, int ChunkIndex);

[RivetContract]
public static class ThingsContract
{
    public static readonly Define UploadChunk =
        Define.Put<UploadChunkInput, ChunkReceipt>("/api/things/{id}/chunks/{chunkIndex}")
            .AcceptsBinary();
}
```

What Rivet enforces — and what it deliberately does not:

- **Spec-only.** Rivet emits the `requestBody` as
  `{ "type": "string", "format": "binary" }` under the declared content type.
  Reading the request stream (`HttpContext.Request.Body`) is host code — Rivet
  never binds or buffers the bytes at runtime.
- **`TInput` lowers to route/query params.** Because the body is the raw bytes,
  the input record's properties never become a JSON body: route-placeholder-matched
  properties bind to the path (`id`, `chunkIndex` above), the rest become query
  parameters — the same lowering GET/DELETE inputs get.
- **Mutually exclusive with other body shapes.** Combining `.AcceptsBinary()` with
  `.AcceptsFile()` or `.FormEncoded()` throws — at runtime from the builder, and at
  generation time from the walker.

On import (`--from-openapi`), any non-multipart request-body content entry whose
schema is `{ "type": "string", "format": "binary" }` — whatever the content type
(`application/octet-stream`, `audio/mpeg`, ...) — scaffolds back as
`.AcceptsBinary("<content-type>")`.

**TypeScript consumers:** `openapi-typescript` types binary request bodies as
`string`. Pass the actual `Blob`/`ArrayBuffer` with a pass-through `bodySerializer`
so the client does not JSON-stringify it:

```ts
await client.PUT("/api/things/{id}/chunks/{chunkIndex}", {
  params: { path: { id, chunkIndex } },
  body: chunk as unknown as string, // Blob — typed as string by openapi-typescript
  bodySerializer: (body) => body as unknown as BodyInit, // pass-through
  headers: { "Content-Type": "application/octet-stream" },
});
```

## Downloads (binary responses)

`Define.File` declares an endpoint that returns binary content instead of JSON
(GET, `application/octet-stream` unless overridden):

```csharp
public static readonly FileRouteDefinition Avatar =
    Define.File("/api/members/{id}/avatar")
        .ContentType("image/jpeg")
        .QueryAuth();             // auth token as ?token=... for media players
```

This emits a `200` response with the given content type and
`{ "type": "string", "format": "binary" }`, plus `x-rivet-query-auth` when
`.QueryAuth()` is used. On non-file definitions, `.ProducesFile(contentType)` does
the same.

`.QueryAuth(parameterName = "token")` adds a required query parameter for the auth
token — for clients (ExoPlayer, HLS.js) that cannot set headers on media segment
requests.

## Enforcement note

`Define.File` definitions have an opt-in `Invoke`: the success branch must carry
file content matching the declared content type (a JSON result on an `image/jpeg`
contract throws `RivetContractViolationException`), and error statuses must be
declared via `.Returns(...)`:

```csharp
[HttpGet("{id}/avatar")]
public async Task<IResult> Avatar(Guid id)
    => await MembersContract.Avatar.Invoke<FileContentHttpResult>(
        async () => TypedResults.File(await store.Load(id), "image/jpeg"));
```

File results write their own status (200, or 206 under range processing), so the
status of the success branch is not checked. Stream *contents* are never inspected,
and handlers that bypass `Invoke` are unchecked. See
[Runtime Validation](/guides/runtime-validation).
