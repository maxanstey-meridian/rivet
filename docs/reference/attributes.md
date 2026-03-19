# Attributes

Rivet provides four marker attributes. All are in the `Rivet.Attributes` NuGet package.

## `[RivetType]`

Marks a type for TypeScript emission. Only needed for types **not reachable** from any endpoint — if a type is used as a request body, response type, or property of such a type, it's discovered transitively.

```csharp
[RivetType]
public sealed record TaskItem(Guid Id, string Title, Priority Priority);
```

Use when you want to share a type between frontend and backend that isn't part of any API endpoint (e.g., a domain event shape, a shared enum, a config DTO).

## `[RivetClient]`

Class-level attribute. All public methods with an HTTP attribute (`[HttpGet]`, `[HttpPost]`, etc.) become endpoints automatically. No per-method annotation needed.

```csharp
[RivetClient]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<TaskDto>>> List(CancellationToken ct) { ... }

    [HttpPost]
    [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTaskCommand command, CancellationToken ct) { ... }
}
```

Best for controllers where every action is an API endpoint.

## `[RivetEndpoint]`

Method-level attribute. Explicit opt-in per method. Works on any class — no base class required.

```csharp
// Standalone endpoint — no controller, no base class
public static class HealthCheck
{
    [RivetEndpoint]
    [HttpGet("/api/health")]
    public static Task<HealthDto> Get() => Task.FromResult(new HealthDto("ok"));
}
```

Use when only some methods on a class should be exposed, or for standalone endpoints outside the controller pattern.

## `[RivetContract]`

Marks a class as an endpoint contract. Two styles:

**Static class (v1)** — with `EndpointBuilder<T>` fields:

```csharp
[RivetContract]
public static class MembersContract
{
    public static readonly EndpointBuilder<List<MemberDto>> List =
        Endpoint.Get<List<MemberDto>>("/api/members")
            .Description("List all team members");
}
```

**Abstract class (v2)** — extending `ControllerBase`:

```csharp
[RivetContract]
[Route("api/tasks")]
public abstract class TasksContract : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<TaskDto>), StatusCodes.Status200OK)]
    public abstract Task<IActionResult> List(CancellationToken ct);
}
```

See the [Contracts guide](/guides/contracts) for detailed usage of both styles.

## Coexistence rules

All four attributes can coexist in the same project. Rivet merges discovered endpoints and deduplicates:

- `[RivetClient]` discovers all HTTP methods on the class
- `[RivetEndpoint]` opts in individual methods
- If a method matches both (e.g., `[RivetEndpoint]` on a method of a `[RivetClient]` class), it's emitted once
- `[RivetContract]` endpoints are merged with controller-sourced endpoints
- On collision (same controller name + method name), the contract definition wins
- `[RivetType]` types are merged with transitively discovered types — no duplicates

## Return type inference

Return types are inferred from (in order of precedence):

1. `[ProducesResponseType(typeof(T), 200)]` — preferred, works with `IActionResult`
2. `ActionResult<T>` — unwrapped automatically
3. `Task<T>` — for static method endpoints

## Route handling

Controller `[Route]` prefixes are combined with method routes. Route constraints (`{id:guid}`) are stripped automatically. Route params without `[FromRoute]` are matched by name from the template. `CancellationToken` and DI params are excluded automatically.

Endpoints are grouped by controller into separate client files: `TasksController` becomes `client/tasks.ts`.
