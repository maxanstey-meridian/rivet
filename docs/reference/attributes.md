# Attributes

Rivet provides four marker attributes. All are in the `Rivet.Attributes` NuGet package.

## `[RivetType]`

Explicitly opts a type into TypeScript emission. **You usually don't need this** — types referenced by endpoints (via `[FromBody]`, `[ProducesResponseType]`, typed returns) and contracts (via `RouteDefinition<T>` generics) are discovered transitively, including everything they reference (nested records, enums, value objects).

```csharp
// This type is NOT referenced by any endpoint or contract,
// but we want it in the generated TypeScript anyway
[RivetType]
public sealed record DomainEvent(string Type, Guid AggregateId, DateTime OccurredAt);
```

Use when a type isn't reachable from any endpoint or contract — e.g., a domain event shape, a shared DTO consumed only by frontend logic, or a generic wrapper that's only instantiated outside endpoint signatures.

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

> **Note:** Rivet reads `[HttpGet]` and other HTTP attributes for code generation, but ASP.NET won't route to a plain static class. To actually serve this endpoint at runtime, register it via minimal APIs (`app.MapGet(...)`) or a controller.

Use when only some methods on a class should be exposed, or for standalone endpoints outside the controller pattern.

## `[RivetContract]`

Marks a class as an endpoint contract. Two styles:

**Static class (v1)** — with `RouteDefinition<T>` fields:

```csharp
[RivetContract]
public static class MembersContract
{
    public static readonly RouteDefinition<List<MemberDto>> List =
        Define.Get<List<MemberDto>>("/api/members")
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
