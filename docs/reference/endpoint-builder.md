# Route Definition API

The `Define` class and `RouteDefinition<T>` types are used in [static class contracts](/guides/contracts) to define endpoint shapes.

## Factory methods

| Method | Signature | Description |
|---|---|---|
| `Define.Get` | `<TInput, TOutput>(route)` | GET with input + output types |
| | `<TOutput>(route)` | GET with output only |
| | `(route)` | GET with no typed I/O |
| `Define.Post` | `<TInput, TOutput>(route)` | POST with input + output types |
| | `<TOutput>(route)` | POST with output only |
| | `(route)` | POST with no typed I/O |
| `Define.Put` | Same overloads as Post | |
| `Define.Patch` | Same overloads as Post | |
| `Define.Delete` | Same overloads as Post | |

## Builder methods

All builder methods return the definition for chaining.

| Method | Description |
|---|---|
| `.Returns<T>(statusCode)` | Declare an additional response type for a status code |
| `.Returns<T>(statusCode, description)` | Same, with a human-readable description |
| `.Status(code)` | Override the default success status code (default: 200) |
| `.Description(desc)` | Endpoint description, emitted to the OpenAPI spec |
| `.Anonymous()` | Marks endpoint as not requiring authentication |
| `.Secure(scheme)` | Sets a named security scheme for the endpoint |
| `.Accepts<T>()` | Convert void definition to input-only (accepts body, returns void) |

## `.Invoke()` — runtime execution

`.Invoke()` executes a handler function and returns `RivetResult<T>`. The compiler enforces that the handler's input/output types match the definition's generic parameters.

### Without input

```csharp
public static readonly RouteDefinition<List<MemberDto>> List =
    Define.Get<List<MemberDto>>("/api/members");

// Usage
var result = await MembersContract.List.Invoke(async () =>
{
    return new List<MemberDto>(); // must return List<MemberDto>
});
```

### With input

```csharp
public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
    Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members");

// Usage
var result = await MembersContract.Invite.Invoke(request, async req =>
{
    // req is InviteMemberRequest, must return InviteMemberResponse
    return new InviteMemberResponse(Guid.NewGuid());
});
```

### Input only (void output)

```csharp
public static readonly InputRouteDefinition<UpdateRoleRequest> UpdateRole =
    Define.Put("/api/members/{id}/role")
        .Accepts<UpdateRoleRequest>()
        .Status(204);

// Usage — typed input, void output
var result = await MembersContract.UpdateRole.Invoke(request, async req =>
{
    // req is UpdateRoleRequest, no return value
});
```

## `RivetResult<T>`

`Invoke` returns `RivetResult<T>` — a framework-agnostic result:

| Property | Type | Description |
|---|---|---|
| `StatusCode` | `int` | HTTP status code |
| `Data` | `T` | Response data |

Convert to your framework's response type with a one-liner bridge:

```csharp
public static class RivetExtensions
{
    public static IActionResult ToActionResult<T>(this RivetResult<T> result)
        => new ObjectResult(result.Data) { StatusCode = result.StatusCode };

    public static IActionResult ToActionResult(this RivetResult result)
        => new StatusCodeResult(result.StatusCode);
}
```

## Parameter classification

How `TInput` properties are classified depends on the HTTP method:

- **GET / DELETE:** Properties matched by name to route template segments become route params. The rest become query params.
- **POST / PUT / PATCH:** Route params come from the template as standalone `string` args. `TInput` becomes the request body.

This matches ASP.NET conventions: `[FromBody] command` + separate `Guid id` route param.
