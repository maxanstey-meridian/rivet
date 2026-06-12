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
