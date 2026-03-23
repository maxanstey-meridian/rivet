# Attributes

Rivet provides marker attributes for discovery and metadata attributes for property-level annotations. All are in the `Rivet.Attributes` NuGet package.

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

## Metadata attributes

Property-level attributes that preserve OpenAPI/JSON Schema metadata through the C# round-trip. Apply with `[property: ...]` syntax on record constructor parameters.

### `[RivetOptional]`

Excludes a property from the `required` array in OpenAPI/JSON Schema output. Without this, all positional constructor parameters are required (even nullable ones — nullable means the value can be null, not that the key can be absent).

```csharp
[RivetType]
public sealed record UserDto(
    string Name,
    [property: RivetOptional] string? Nickname);  // not in "required"
```

### `[RivetDescription]`

Adds a `description` field to the property or type schema. Can be applied at both type level and property level.

```csharp
[RivetDescription("A user in the system")]
[RivetType]
public sealed record UserDto(
    [property: RivetDescription("The user's full name")] string Name,
    [property: RivetDescription("Account email address")] string Email);
```

### `[RivetConstraints]`

Preserves OpenAPI validation constraints. All properties are optional — set only what applies.

```csharp
[RivetType]
public sealed record ProductDto(
    [property: RivetConstraints(MinLength = 1, MaxLength = 200)] string Name,
    [property: RivetConstraints(Minimum = 0, Maximum = 999.99, MultipleOf = 0.01)] double Price,
    [property: RivetConstraints(MinItems = 0, MaxItems = 50)] List<string> Tags);
```

Available constraint properties: `MinLength`, `MaxLength`, `Pattern`, `Minimum`, `Maximum`, `ExclusiveMinimum`, `ExclusiveMaximum`, `MultipleOf`, `MinItems`, `MaxItems`, `UniqueItems`.

### `[RivetDefault]`

Preserves a default value as a JSON literal string.

```csharp
[RivetType]
public sealed record ConfigDto(
    [property: RivetDefault("\"en\"")] string Locale,
    [property: RivetDefault("25")] int PageSize);
```

### `[RivetExample]`

Preserves an example value as a JSON literal string.

```csharp
[RivetType]
public sealed record UserDto(
    [property: RivetExample("\"jane@example.com\"")] string Email);
```

### `[RivetFormat]`

Preserves a custom OpenAPI format string for types where there's no dedicated C# mapping (e.g., `uri-template`, `phone-number`).

```csharp
[RivetType]
public sealed record LinkDto(
    [property: RivetFormat("uri-template")] string Href);
```

### `[RivetReadOnly]` / `[RivetWriteOnly]`

Mark properties as read-only or write-only in the schema.

```csharp
[RivetType]
public sealed record UserDto(
    [property: RivetReadOnly] string Id,
    [property: RivetWriteOnly] string Password,
    string Name);
```

### Combined example

```csharp
[RivetDescription("A product listing")]
[RivetType]
public sealed record ProductDto(
    [property: RivetReadOnly]
    [property: RivetDescription("Unique identifier")]
    string Id,

    [property: RivetConstraints(MinLength = 1, MaxLength = 200)]
    [property: RivetDescription("Product name")]
    [property: RivetExample("\"Widget Pro\"")] string Name,

    [property: RivetDefault("9.99")]
    [property: RivetConstraints(Minimum = 0)]
    double Price,

    [property: RivetOptional]
    [property: RivetWriteOnly]
    string? InternalNotes);
```

These attributes are emitted to both OpenAPI and JSON Schema output, and are preserved through OpenAPI import round-trips.
