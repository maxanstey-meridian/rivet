# Contract-Driven Endpoints

Instead of annotating controllers directly, you can define endpoint shapes in standalone contract classes. Rivet reads them at generation time for TS codegen, and your controllers implement them for runtime execution.

Two styles — both generate the same TypeScript output.

## Static class contracts (v1)

A `[RivetContract]` static class with `RouteDefinition<T>` fields. No ASP.NET dependency — pure Rivet types.

```csharp
using Rivet;

[RivetContract]
public static class MembersContract
{
    // GET /api/members → List<MemberDto>
    public static readonly RouteDefinition<List<MemberDto>> List =
        Define.Get<List<MemberDto>>("/api/members")
            .Summary("List all team members");

    // POST /api/members — typed input + output, 201 is default for POST, 422 on validation failure
    public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
        Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Summary("Invite a new team member")
            .Returns<ValidationErrorDto>(422, "Validation failed")
            .Secure("admin");

    // DELETE /api/members/{id} — no typed I/O, defaults to 204, 404 response declared
    public static readonly RouteDefinition Remove =
        Define.Delete("/api/members/{id}")
            .Summary("Remove a team member")
            .Returns<NotFoundDto>(404, "Member not found")
            .Returns(409)  // void error response — no body
            .Secure("admin");

    // GET /api/health — no auth required
    public static readonly RouteDefinition Health =
        Define.Get("/api/health")
            .Summary("Health check")
            .Anonymous();
}
```

This generates the same typed client as annotation-based controllers:

```typescript
// Generated client/members.ts

export function list(): Promise<MemberDto[]>;
export function list(opts: { unwrap: false }): Promise<RivetResult<MemberDto[]>>;

export type InviteResult =
  | { status: 201; data: InviteMemberResponse; response: Response }
  | { status: 422; data: ValidationErrorDto; response: Response };

export function invite(input: { body: InviteMemberRequest; }): Promise<InviteMemberResponse>;
export function invite(input: { body: InviteMemberRequest; }, opts: { unwrap: false }): Promise<InviteResult>;

export type RemoveResult =
  | { status: 204; data: void; response: Response }
  | { status: 404; data: NotFoundDto; response: Response }
  | { status: 409; data: void; response: Response };

export function remove(input: { params: { id: string; }; }): Promise<void>;
export function remove(input: { params: { id: string; }; }, opts: { unwrap: false }): Promise<RemoveResult>;

export function health(): Promise<void>;
```

### Using contracts at runtime with `.Invoke()`

Controllers call `.Invoke()` on the contract fields for type-safe execution. The compiler enforces that input/output types match:

```csharp
[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => (await MembersContract.List.Invoke(async () =>
        {
            // Must return List<MemberDto> — compiler enforced
            return new List<MemberDto>();
        })).ToActionResult();

    [HttpPost]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request, CancellationToken ct)
        => (await MembersContract.Invite.Invoke(request, async req =>
        {
            // req is InviteMemberRequest, must return InviteMemberResponse
            return new InviteMemberResponse(Guid.NewGuid());
        })).ToActionResult();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
        => (await MembersContract.Remove.Invoke(async () =>
        {
            // void endpoint — no return value
        })).ToActionResult();
}
```

`Invoke` returns `RivetResult<T>` — a framework-agnostic result with status code and typed data. You provide a one-liner bridge to convert it to your framework's response type:

```csharp
// Write once per project
public static class RivetExtensions
{
    public static IActionResult ToActionResult<T>(this RivetResult<T> result)
        => new ObjectResult(result.Data) { StatusCode = result.StatusCode };

    public static IActionResult ToActionResult(this RivetResult result)
        => new StatusCodeResult(result.StatusCode);
}
```

### File download endpoints

Use `.ProducesFile()` for endpoints that return binary content instead of JSON. No `TOutput` type parameter — the success response is a file, not a typed DTO.

```csharp
[RivetContract]
public static class DocumentsContract
{
    public static readonly RouteDefinition GetDocument =
        Define.Get("/api/documents/{id}")
            .Description("Download a document")
            .ProducesFile()  // default: application/octet-stream
            .Returns<ErrorDto>(404, "Document not found");
}
```

```typescript
// Generated — returns Blob, error responses still typed
export function getDocument(input: { params: { id: string; }; }): Promise<Blob>;
export function getDocument(input: { params: { id: string; }; }, opts: { unwrap: false }): Promise<GetDocumentResult>;
```

The content type defaults to `application/octet-stream`. Pass a specific type for known formats:

```csharp
.ProducesFile("application/pdf")
```

::: info Note
`.ProducesFile()` is metadata-only — it affects codegen and OpenAPI emission but has no runtime `Invoke()` behavior. File download controllers wire the response manually (e.g. `File(bytes, contentType, fileName)`).
:::

#### Named file downloads with `[ProducesFile]`

When the handler needs to return both file content and a filename through `.Invoke()`, use the `[ProducesFile]` attribute with a `(byte[], string)` tuple:

```csharp
[RivetContract]
public static class DocumentsContract
{
    [ProducesFile]
    public static readonly RouteDefinition<(byte[] Content, string FileName)> GetDocument =
        Define.Get<(byte[] Content, string FileName)>("/api/documents/{id}")
            .Description("Download a document");
}
```

The walker recognises the attribute and emits the same `application/octet-stream` + `format: binary` schema. The tuple lets the handler return both pieces through the contract's lambda, while the bridge extension unpacks them into the framework's file response.

`[ProducesFile]` also works on plain `byte[]` fields — equivalent to calling `.ProducesFile()` in the builder chain.

### Controller naming

The contract class name maps to the client file: `MembersContract` → `client/members.ts` (strips the `Contract` suffix and camelCases, same as `MembersController` → `client/members.ts`).

### Parameter classification

For `GET`/`DELETE`, `TInput` properties are matched by name to route template segments (→ route params), with the rest becoming query params. For `POST`/`PUT`/`PATCH`, route params come from the template as standalone `string` args, and `TInput` becomes the request body. This matches how ASP.NET controllers work — `[FromBody] command` + separate `Guid id` route param.

## Abstract base class contracts (v2)

A `[RivetContract]` abstract class extending `ControllerBase`. HTTP attributes on abstract methods, `[ProducesResponseType]` for responses. Your controller inherits and overrides — the compiler enforces signatures.

```csharp
[RivetContract]
[Route("api/tasks")]
public abstract class TasksContract : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<TaskDto>), StatusCodes.Status200OK)]
    public abstract Task<IActionResult> List(CancellationToken ct);

    [HttpPost]
    [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationError), StatusCodes.Status422UnprocessableEntity)]
    public abstract Task<IActionResult> Create(
        [FromBody] CreateTaskCommand command, CancellationToken ct);
}
```

```csharp
// Controller inherits — compiler enforces signatures
public sealed class TasksController : TasksContract
{
    public override async Task<IActionResult> List(CancellationToken ct)
    {
        // Implementation
    }

    public override async Task<IActionResult> Create(
        [FromBody] CreateTaskCommand command, CancellationToken ct)
    {
        // Implementation
    }
}
```

### Why abstract class, not interface?

ASP.NET attribute inheritance. `[HttpGet]`, `[Route]`, and `[ProducesResponseType]` are inherited from abstract base classes but not from interfaces. This means the implementing controller doesn't need to redeclare any routing or response metadata — zero duplication.

### Existing inheritance chains

If your controllers already extend a base class, the contract can sit in between:

```
ExistingBase → TasksContract → TasksController
```

## Which style to use

| | Static class (v1) | Abstract class (v2) |
|---|---|---|
| **ASP.NET dependency** | None — pure Rivet types | Requires `ControllerBase` |
| **Runtime execution** | `.Invoke()` with `RivetResult<T>` | Standard controller overrides |
| **Type enforcement** | Compile-time via `Invoke` generics | Compile-time via abstract methods |
| **Duplication** | Contract + controller attributes | Zero — controller inherits everything |
| **Portability** | Can be used outside ASP.NET | Tied to ASP.NET |
| **OpenAPI import** | Generated by `--from-openapi` | Not generated |

Both styles generate the same TypeScript output. Contracts and controller attributes can coexist in the same project; if both define the same endpoint (matching controller name + method name), the contract wins.

## Coexistence

`[RivetContract]`, `[RivetClient]`, and `[RivetEndpoint]` can all coexist in the same project. Rivet merges the discovered endpoints and deduplicates — if a contract and a controller annotation define the same endpoint (same controller name + method name), the contract definition wins.

See the [Route Definition](/reference/endpoint-builder) reference for the full builder API, and the [sample projects](https://github.com/maxanstey-meridian/rivet/tree/main/samples) for working examples.
