# Contract-Driven Endpoints

Instead of annotating controllers directly, you can define endpoint shapes in standalone contract classes. Rivet reads them at generation time for TS codegen, and your controllers implement them for runtime execution.

Two styles — both generate the same TypeScript output.

## Static class contracts (v1)

A `[RivetContract]` static class with `EndpointBuilder<T>` fields. No ASP.NET dependency — pure Rivet types.

```csharp
[RivetContract]
public static class MembersContract
{
    public static readonly EndpointBuilder<List<MemberDto>> List =
        Endpoint.Get<List<MemberDto>>("/api/members")
            .Description("List all team members");

    public static readonly EndpointBuilder<InviteMemberRequest, InviteMemberResponse> Invite =
        Endpoint.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Description("Invite a new team member")
            .Status(201)
            .Returns<InviteMemberResponse>(422, "Validation failed")
            .Secure("admin");

    public static readonly EndpointBuilder Remove =
        Endpoint.Delete("/api/members/{id}")
            .Description("Remove a team member")
            .Returns<MemberDto>(404, "Member not found")
            .Secure("admin");
}
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
            // Must return List<MemberDto> — compile error if wrong
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

See the [Endpoint Builder](/reference/endpoint-builder) reference for the full builder API, and the [sample projects](https://github.com/maxanstey-meridian/rivet/tree/main/samples) for working examples.
