# File Uploads & Downloads

## Uploads (multipart/form-data)

On controller endpoints, an `IFormFile` (or `List<IFormFile>`) parameter makes the
operation a `multipart/form-data` request body; the file property is emitted as
`{ "type": "string", "format": "binary" }` and other parameters are classified
alongside it (route params stay in the path, scalars join the form body).

On contracts, mark the definition with `.AcceptsFile()`:

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

`Define.File` definitions have **no runtime enforcement**: there is no `Invoke`, so
content type, stream contents, and status codes are entirely your handler's
responsibility. See [Runtime Validation](/guides/runtime-validation).
