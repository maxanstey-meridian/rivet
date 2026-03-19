# Endpoint Builder API

The `Endpoint` class and `EndpointBuilder<T>` types are used in [static class contracts](/guides/contracts) to define endpoint shapes.

## Factory methods

| Method | Signature | Description |
|---|---|---|
| `Endpoint.Get` | `<TInput, TOutput>(route)` | GET with input + output types |
| | `<TOutput>(route)` | GET with output only |
| | `(route)` | GET with no typed I/O |
| `Endpoint.Post` | `<TInput, TOutput>(route)` | POST with input + output types |
| | `<TOutput>(route)` | POST with output only |
| | `(route)` | POST with no typed I/O |
| `Endpoint.Put` | Same overloads as Post | |
| `Endpoint.Patch` | Same overloads as Post | |
| `Endpoint.Delete` | Same overloads as Post | |

## Builder methods

All builder methods return the builder for chaining.

| Method | Description |
|---|---|
| `.Returns<T>(statusCode)` | Declare an additional response type for a status code |
| `.Returns<T>(statusCode, description)` | Same, with a human-readable description |
| `.Status(code)` | Override the default success status code (default: 200) |
| `.Description(desc)` | Endpoint description, emitted to the OpenAPI spec |
| `.Anonymous()` | Marks endpoint as not requiring authentication |
| `.Secure(scheme)` | Sets a named security scheme for the endpoint |

## `.Invoke()` — runtime execution

`.Invoke()` executes a handler function and returns `RivetResult<T>`. The compiler enforces that the handler's input/output types match the builder's generic parameters.

### Without input

```csharp
public static readonly EndpointBuilder<List<MemberDto>> List =
    Endpoint.Get<List<MemberDto>>("/api/members");

// Usage
var result = await MembersContract.List.Invoke(async () =>
{
    return new List<MemberDto>(); // must return List<MemberDto>
});
```

### With input

```csharp
public static readonly EndpointBuilder<InviteMemberRequest, InviteMemberResponse> Invite =
    Endpoint.Post<InviteMemberRequest, InviteMemberResponse>("/api/members");

// Usage
var result = await MembersContract.Invite.Invoke(request, async req =>
{
    // req is InviteMemberRequest, must return InviteMemberResponse
    return new InviteMemberResponse(Guid.NewGuid());
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
