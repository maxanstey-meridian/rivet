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
(or collection-of-`IFormFile`) properties, is detected as multipart automatically. A
record behind `.FormEncoded()` is a form too, just without the files. Either way, which
properties count, and which become parts, is one list, read the way ASP.NET's form binders
read it, so a file that makes the body multipart is always one of its parts:

- the instance properties with a public getter, along the whole base chain, a base class
  from a NuGet package included (both binders list members through their getters, so a
  `{ private get; set; }` property is invisible to them);
- a derived property hides its base's of the same name, whatever its accessibility, so an
  `internal new string File` takes the base's `IFormFile File` out of the form, as it
  does at runtime;
- `[JsonIgnore]` doesn't count, because the form binders never read it: an ignored
  `IFormFile` is still a part, and so is an ignored text field;
- route-bound and `[RivetHeader]` properties are left to the route and the headers, a
  `[RivetReadOnly]` field is never sent, and a `[JsonExtensionData]` bag or a field nothing
  can set (a computed `=> ...`, a private setter) sends no part at all;
- a file nothing can set is refused (`RIV1100`) rather than left out, since it would still
  make the body multipart for a part no binder reads. Give it a public setter or an `init`.

Which members a binder sets depends on how it creates the type, and the two don't agree:

- **MVC** calls a record class's one public constructor when every parameter matches a
  property by its exact name and type, and otherwise needs a public parameterless
  constructor. A class whose only constructor takes parameters fails every request, and so
  does an abstract class, a struct, or a record struct (reflection lists no implicit
  parameterless constructor, and a record struct isn't a record class to MVC).
- **The form mapper** calls a type's one public constructor, whatever it is. Give it two,
  or make it abstract, and it binds nothing at all.
- MVC also fills a get-only collection in place: `public List<IFormFile> Files { get; } = [];`
  gets every file, because MVC adds them to the list the getter hands back. The form mapper
  can't set it, so on a contract that's `RIV1100` until it has a setter or an `init`.
- MVC skips a `[BindNever]` member; the form mapper skips an `[IgnoreDataMember]` one.
  Each ignores the other's attribute.

So a form input a host can't create stops generation with `RIV1100`, and on a contract so
does a member only one host skips. Skip a member for both, with both attributes, and it's
simply no part.

The body `$ref`s the input's component when the component lists exactly those parts under
the same names, and is spelled out inline when it doesn't (a file on a package base the
type walk stops at, an ignored member, a property bound to the route, a part whose name
isn't its JSON name). Inline, each part still carries its property's schema and
annotations (description, bounds, default, example) as it would in a JSON body.

On a `[RivetEndpoint]` method, a `[FromForm]` record parameter goes the same way: its members
become the parts, so a record with an `IFormFile` in it makes the body multipart instead of a
urlencoded body that can't carry a file. An instance method is an MVC action, and a `Name =`
on the parameter is a prefix MVC then reads every part under
(`[FromForm(Name = "p")] UploadForm form` wants `p.Title`). A static method is a minimal-API
handler, whose form mapper never prefixes: it wants `Title` either way.

### Part names

A form part is named by what the server's binder reads, not by the JSON surface. Neither
of ASP.NET's form binders has ever heard of `[JsonPropertyName]`:

- **MVC's model binder** reads a member under its `[FromForm(Name = ...)]` (or
  `[BindProperty(Name = ...)]`), else its C# name. On a positional record that means the
  constructor parameter's attribute: a `[property: FromForm(Name = ...)]` is never seen.
- **The minimal-API form mapper** reads a member under its `[DataMember(Name = ...)]`, else
  its C# name, and ignores every binding attribute on the member.
- Both ignore case, so `Title` is happy to answer to `title`.

A contract can be served by either host, so its part names must be ones both read. With no
rename, that's the C# name, camelCased like everything else. A `[JsonPropertyName]` on a form
member changes nothing on the wire, so it changes nothing in the spec either (it still names
the property in the component, and an input whose part names differ from its component's is
spelled out inline). To send a part under a name C# can't spell, rename it for both
binders at once:

```csharp
[RivetType]
public sealed record UploadForm
{
    [DataMember(Name = "file_name")]  // the minimal-API form mapper
    [FromForm(Name = "file_name")]    // MVC
    public required IFormFile FileName { get; init; }
}
```

A rename only one binder reads would leave the other host binding nothing, and no single
part name in the spec can be right for both, so generation stops with `RIV1100` instead of
picking one. So does a form member with a `[FromQuery]`, `[FromRoute]`, `[FromHeader]` or
`[FromBody]` of its own: MVC would read it from there, the form mapper from the form. A
`[RivetEndpoint]` method has one host, so it needs one host's rename: an instance method
runs on MVC, where a `[FromForm(Name = ...)]` alone is enough and a `[DataMember]` is
ignored; a static one runs on the minimal-API form mapper, where it's the other way round.

`--from-openapi` writes exactly that pair for a form part whose name isn't its property's
(`file_name`, `x-meta`), beside the `[JsonPropertyName]`, and writes form inputs in the
`{ get; init; }` form: the minimal-API form mapper refuses a request that leaves out any
constructor parameter of a positional record, nullable or not.

That refusal shapes the spec too. A contract's positional form record lists every
constructor-bound part as `required`, `[RivetOptional]` or nullable as it may be, because
that's the only request both hosts accept: MVC would bind a missing one as `null`, the form
mapper answers `400`. The record's component still calls the member optional (a JSON body
really can leave it out), so such a body is spelled out inline rather than `$ref`'d. A
static `[RivetEndpoint]` method runs on the form mapper alone and lists them as required
too; an instance one runs on MVC alone and keeps the member's own optionality. If a part
should genuinely be optional on the wire, make it an `init` property instead of a
constructor parameter.

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
    Define.File("/api/members/{id:guid}/avatar")
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
`Stream`, an absolute physical path, and a [writer](#files-written-as-they-go).
Input-bearing file definitions first use `.Bind(input)`. Errors use `.Error(...)` and
must be declared via `.Returns(...)`:

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

## Files written as they go

Some bodies don't exist until you send them, like ffmpeg transcoding straight into
the response. Hand `.File(...)` a writer instead of content and Rivet gives it the
response stream once the contract checks have passed:

```csharp
public static readonly FileRouteDefinition<ClipInput> Live = Define
    .File<ClipInput>("/clips/{id}/live")
    .ProducesFile("video/*");

[HttpGet("{id}/live")]
public IActionResult Stream([FromRoute] ClipInput input) =>
    Live.Bind(input)
        .File(
            output => transcoder.RunAsync(input.Id, output, HttpContext.RequestAborted),
            contentType: "video/mp4"
        )
        .ToActionResult();
```

The content type, download name, `inline` and `nosniff` rules are the ones every other
file follows, and a bad content type throws before the writer runs, so ffmpeg never
starts for a response Rivet was going to refuse. What you give up is ranges: there's
no `enableRangeProcessing`, because a stream that's still being made can't jump to
byte 4,000,000. Seeking is your API's business, typically a start offset in the query
that begins a fresh stream.

Two things catch people out. The writer runs after your handler has returned, so
anything it needs (a process, a database reader) has to be opened inside the writer,
not in a `using` in the handler, which has already disposed it by then. And write with
async APIs: Kestrel refuses synchronous writes to the response, so a library that
writes synchronously throws on its first synchronous write: a 500 if nothing has gone
out yet, a body cut short if something has.

## Choosing a binary response MIME type

When an endpoint can return different file formats, declare each representation:

```csharp
public static readonly FileRouteDefinition Image = Define.File("/images/{id:guid}")
    .ProducesFile("image/png")
    .ResponseBinaryContent(200, "image/jpeg");
```

Select the actual representation per response:

```csharp
return Image.File(bytes, contentType: "image/jpeg").ToActionResult();
```

The selector works with byte arrays, streams, physical paths, writers, and bound
file routes. It must match a declared binary success representation (case-insensitive,
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
