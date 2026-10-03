# File Uploads & Downloads

## Uploads (multipart/form-data)

On controller endpoints, an `IFormFile` parameter — or a collection of them
(`List<IFormFile>`, `IFormFile[]`, `IReadOnlyList<IFormFile>`, ...) — makes the
operation a `multipart/form-data` request body; a single file property is emitted as
`{ "type": "string", "format": "binary" }` and a collection as an array of binary
parts. `IFormFile` itself is the retained semantic convention; it does not pull
neighbouring parameters into the form body. Every other form field must declare its
source explicitly: `[FromForm] string caption` becomes a form field beside the file,
`[FromRoute]`/`[FromQuery]`/`[FromHeader]` parameters keep their declared locations,
and any unattributed neighbour refuses extraction (RIV1100). A complex type declared
`[FromForm]` beside a file refuses too (RIV1104) — lowering it faithfully would mean
emulating MVC's recursive form binder, and a `[FromBody]` body cannot ride a multipart
request at all.

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
    public static readonly RouteDefinition<UploadChunkInput, ChunkReceipt> UploadChunk =
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
        .ProducesFile("image/jpeg")
        .QueryAuth();             // auth token as ?token=... for media players
```

This emits a `200` response with the given content type and
`{ "type": "string", "format": "binary" }`, plus `x-rivet-query-auth` when
`.QueryAuth()` is used. On non-file definitions, `.ProducesFile(contentType)` does
the same.

`.QueryAuth(parameterName = "token")` adds a required query parameter for the auth
token — for clients (ExoPlayer, HLS.js) that cannot set headers on media segment
requests.

## Returning files

Run the application operation normally, then construct the response with `.File(...)`.
Rivet takes the content type from the contract and provides overloads for `byte[]`,
`Stream`, and an absolute physical path. Input-bearing file definitions first use
`.Bind(input)`. Errors use `.Error(...)` and must be declared via `.Returns(...)`:

```csharp
[HttpGet("{id}/avatar")]
public async Task<IActionResult> Avatar(Guid id)
{
    var content = await store.Load(id);
    return MembersContract.Avatar.File(content, "avatar.jpg").ToActionResult();
}
```

`.ToActionResult()` and `.ToResult()` are the first-party MVC and minimal-API
bridges. Range processing can produce `206`; stream *contents* are never inspected,
and handlers that bypass the contract terminals are unchecked. See [Runtime
Validation](/guides/runtime-validation).

## Choosing a binary response MIME type

When an endpoint can return different file formats, declare each representation:

```csharp
public static readonly FileRouteDefinition Image = Define.File("/images/{id}")
    .ProducesFile("image/png")
    .ResponseBinaryContent(200, "image/jpeg");
```

Select the actual representation per response:

```csharp
return Image.File(bytes, contentType: "image/jpeg").ToActionResult();
```

The selector works with byte arrays, streams, physical paths, and bound file
routes. It must match a declared binary success representation (case-insensitive,
ignoring parameters such as `charset`). It does not negotiate `Accept` or inspect
file bytes. Omit it only when exactly one concrete representation is declared;
ambiguous and undeclared selections throw `RivetContractViolationException`.

## Files whose type is only known at runtime

Stored uploads can be any allowed type. Declare a media range and pass the concrete
type each response serves:

```csharp
public static readonly FileRouteDefinition<FileInput> Download = Define
    .File<FileInput>("/files/{id}")
    .ProducesFile("*/*");

return Download.Bind(input).File(stream, file.Name, contentType: file.ContentType).ToActionResult();
```

A declared range (`*/*`, `image/*`) admits any concrete type inside it, and that type
is what reaches the wire. An exact declaration wins over a range. A range rejects a
type outside it, a wildcard or malformed runtime type, and an omitted `contentType`.

## Inline files and sniffing

Every file response carries `X-Content-Type-Options: nosniff`: the contract states the
type, so the browser must not guess another. A download name normally makes the file
an attachment. Pass `inline: true` to show it in the browser and keep the name:

```csharp
return Download.Bind(input)
    .File(stream, file.Name, contentType: file.ContentType, inline: true)
    .ToActionResult();
```

Choosing which types are safe to show inline (for example, not `image/svg+xml`, which
runs script) is the application's decision.
