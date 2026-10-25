# Contract Coverage

`--check` verifies that every `[RivetContract]` field is actually implemented, and
that the implementation matches the declaration:

```bash
dotnet rivet --project path/to/Api.csproj --check
```

The checker finds `.Success(...)`, `.Error(...)`, and `.File(...)` terminal calls for
each contract field, including terminals reached through `.Bind(input)`, in
controller actions, minimal API handlers (`MapGet`/`MapPost`/`MapPut`/
`MapDelete`/`MapPatch` lambdas), and isolated Azure Functions using
`[Function]`, `[HttpTrigger]`, and `.ToActionResult()`. Functions coverage reads
the project's `host.json` HTTP prefix (default `api`) and honors the host
environment override. See [Azure Functions](./azure-functions) for conventions
and a real-host test.

A `Map*` route can be a string constant or the contract field's own `.Route`, as in
`app.MapGet(MembersContract.Health.Route, …)`, and `MapGroup` prefixes count either
way: the contract owns the full path, so a group prefix it doesn't declare is a
`RouteMismatch`. Anything else (a variable, concatenation, interpolation, or a
`MapGroup` prefix that isn't a constant) is reported as an unresolved
`RouteMismatch`, because `--check` won't guess a route.

`--check` reports six kinds of finding on stderr. The first four are warnings; `UnbindableEnumParameter` (`RIV4005`) and `InputBindingMismatch` (`RIV4006`) are errors:

| Finding | Meaning |
|---|---|
| `MissingImplementation` | No contract response terminal found for the contract field. |
| `OrphanedBinding` | The contract was bound in a route handler, but the handler neither returns a terminal built from that binding nor passes the binding to a helper whose answer it returns. |
| `HttpMethodMismatch` | The implementing endpoint uses a different HTTP method than the contract declares. |
| `RouteMismatch` | The implementing endpoint's route does not match the contract's route template. |
| `UnbindableEnumParameter` | The handler binds an enum from the route, query, header or form (directly, or as a member of a complex parameter the host binds member by member), and its host would refuse one of the enum's advertised wire values. See [below](#enum-parameters-the-host-can-t-bind). |
| `InputBindingMismatch` | The handler binds an input the contract doesn't declare, never binds one it does, or binds it from somewhere else. See [below](#inputs-the-handler-doesn-t-bind-where-the-contract-says). |

A summary line is always printed:

```
Coverage: 6/6 endpoints covered. All OK.
```

## Enum parameters the host can't bind

One more check reads the handler's parameters rather than its terminals. When a
handler binds an enum from the route, query, header or form, the spec lists the
enum's wire values for that parameter, but it's ASP.NET doing the binding, not
Rivet's JSON converter. Minimal APIs only take a value that equals the member name
exactly. MVC takes one that equals it ignoring case. If any wire value won't bind on
the handler's host, `--check` fails with `RIV4005`:

```csharp
[JsonConverter(typeof(RivetCamelCaseEnumConverter<Kind>))]
public enum Kind { Footpath, RestrictedByway }

// The spec says ?kind=restrictedByway. Enum.TryParse is case-sensitive: 400.
app.MapGet(PathsContract.List.Route, (Kind kind) => …);
```

The same handler as an MVC action passes, since `restrictedByway` matches ignoring
case. Snake and kebab converters, and a `[JsonStringEnumMemberName]` that changes
more than the casing, fail on both. A plain enum, the built-in
`JsonStringEnumConverter<T>` with no renames, or a handler that binds a `string` and
parses it itself are fine on both. Azure Functions is stricter still: the worker reads
a route value as JSON, so no name binds at all, and only a plain enum or a `string`
will do. The full story, including what the hosts accept beyond the spec, is in
[Limitations](/misc/limitations#enum-parameters-are-bound-by-asp-net).

## Inputs the handler doesn't bind where the contract says

The spec tells every client where to put each input: `{id}` in the path, `?q=` in the
query, `X-Tenant` in a header, the rest in the body. ASP.NET doesn't read the spec. It
binds what the handler's parameters ask for, by their names and attributes. When the two
drift apart the request still compiles, still routes, and quietly loses a value.
`--check` lines them up and fails with `RIV4006` when they disagree:

```csharp
[RivetType]
public sealed record NoteSearch([property: JsonPropertyName("q")] string? Text);

// The spec says ?q=. [AsParameters] binds the member by its C# name: ?Text=.
app.MapGet(NotesContract.Search.Route, ([AsParameters] NoteSearch input) => …);
```

That one reports twice: the handler binds a query parameter `Text` the contract never
declares, and never binds the `q` it does. `[FromQuery(Name = "q")] string? text` fixes
it. Three things are reported:

- **An input the contract doesn't declare.** No client generated from the spec sends
  it, so it's always missing, and a required one fails every request.
- **An input the contract declares that the handler never binds.** Clients send it and
  the handler ignores it. A route value counts too: `{id}` in the contract wants an `id`
  in the handler.
- **The right name from the wrong place**, such as `[FromHeader] string search` for a
  query parameter `search`.

The body counts as one input. An MVC action without `[ApiController]` binds an
unattributed complex parameter from the route, query and form, never the body, so a
contract body there needs `[FromBody]`; under `[ApiController]` the same parameter *is*
the body, so a GET whose query record lacks `[FromQuery]` reads an empty body instead
of the query. A `.FormEncoded()` body is different: its fields travel as named form
values, so each one counts as an input of its own, under the name the spec gives it. A
`[FromForm]` record whose `Body` property the spec calls `note_body` reports both halves,
just like the query example above.

Names compare ignoring case, as ASP.NET's do, and the check follows the same binding rules
as `RIV4005`: explicit `[From*]` attributes and their `Name =`, `[AsParameters]`, and each
host's inference. On MVC, a `Name =` on a complex parameter is the prefix its members are
read under, so `[FromQuery(Name = "f")] NoteSearch filter` wants `?f.Search=`, which no
client sends. Minimal APIs read a `StringValues` like a `string[]`, from the query on a GET,
and never consult a `[TypeConverter]`, so a type that only has one is a service or a body
there. And because every MVC answer depends on whether `[ApiController]` infers binding
sources, a `SuppressInferBindingSourcesForParameters` set to anything but a constant stops
the check with `RIV1115` instead of letting it guess. Members MVC reads under a prefix keep
it when renamed (`?f.q=`), and header members never take one.

A handler that can read the request itself is trusted to pick inputs out by hand: an
`HttpContext` or `HttpRequest` parameter, a `BindAsync` type, a custom model binder, or a
handler body that reads the request or route data through a property (a controller's
`Request` or `RouteData`, `ControllerContext.HttpContext.Request`, an
`IHttpContextAccessor`'s `HttpContext.Request`), calls `HttpContext.GetRouteValue`, or
hands the `HttpContext` or `HttpRequest` to another method (`HttpContext.GetTenantId()`).
Touching only `HttpContext.User`, `Response` or `RequestAborted` doesn't count. Helpers it
calls aren't followed. Such a handler is never told an input is unbound, though what it
does bind is still checked. That's also the only way to read a cookie the contract
declares: ASP.NET has no attribute that binds one, so `--check` reports a declared cookie
unless the handler can read `Request.Cookies`. An unattributed complex parameter that
could be a service counts as the body when the contract has one and as a service when it
doesn't. The `Authorization` header, a `.QueryAuth()` token and the header or query
parameter of an apiKey scheme the endpoint's security names (its own `.Secure(...)`, or
the document's global security, from attributes or `--security`) belong to the contract's
security, not its inputs; `--check` takes the same `--security` values as generation.
Azure Functions handlers aren't compared: they read the query, headers and
body from `HttpRequest` by hand, so only their route values are bound at all.

## Helpers are fine

The spec comes from the contract, never from handler code. Every response is declared
with `.Returns(...)` on the field, so moving an `.Error(...)` or a 404 guard into a
helper changes nothing in the OpenAPI output. A helper can take the bound endpoint
(`BoundRouteDefinitionBase`, or the bound definition type) and return a `RivetResult`.
Rivet still checks the status against that endpoint's contract at runtime.

`--check` reads handler bodies for one thing only: each handler must return at least one
terminal on its own contract field, usually the `.Success(...)`. That's how it knows the
endpoint is implemented on the right verb and route. The rest can live wherever you like:

```csharp
app.MapGet(TasksContract.GetTask.Route, async (Guid id, TaskStore tasks) =>
{
    var endpoint = TasksContract.GetTask.Bind(new TaskLookup(id));
    return await tasks.Find(id) is { } task
        ? endpoint.Success(task).ToResult()
        : NotFound(endpoint).ToResult();
});

app.MapGet(TasksContract.ListComments.Route, async (Guid id, TaskStore tasks) =>
{
    var endpoint = TasksContract.ListComments.Bind(new TaskLookup(id));
    return await tasks.Comments(id) is { } comments
        ? endpoint.Success(comments).ToResult()
        : NotFound(endpoint).ToResult();
});

static RivetResult NotFound(BoundRouteDefinitionBase endpoint) =>
    endpoint.Error(StatusCodes.Status404NotFound, new ErrorDto("Task not found"));
```

Both fields declare `.Returns<ErrorDto>(404)`. The helper doesn't care which endpoint
it's answering for, because the bound endpoint carries its own contract.

Handing the bound endpoint to a helper also counts as using it, so the binding isn't
orphaned. That holds when the helper call is part of what the handler returns, at any
depth: `NotFound(endpoint).ToResult()`, your own extension in a `Match` arm such as
`refused: r => endpoint.Refuse(r)`, or one wrapped in another helper, as in
`Done(endpoint.Refuse(r))`. An arm that binds its own input and hands it straight over
counts too, as in `missing: reason => TasksContract.CreateTask.Bind(new(reason)).Refuse(422)`.
`--check` looks through the same things it does for terminals (see
[below](#how-a-handler-can-return-its-terminal)): an `await`, a cast, a `switch` or `?:`,
and a local holding the helper's answer. A helper call whose answer is thrown away, or one
inside a lambda that the returned call doesn't take directly, still leaves the binding
orphaned.

What a helper can't do is hold a handler's *only* terminal. Using the binding and covering
the field are different things: `--check` gives a field credit only for a terminal the
handler returns through `.ToResult()` or `.ToActionResult()` itself. A handler whose every
answer comes from helpers isn't tied to any field, so if nothing else implements that
field, `--check` reports it as `MissingImplementation`, binding or no binding.

## How a handler can return its terminal

"Return a terminal" covers the ways handlers actually get written. `--check` follows the
value from the handler's `return` to the `.ToResult()` or `.ToActionResult()` call, and from
there back to the terminal. All of these count:

```csharp
// An adapter on each arm of a switch or ?:
return found ? endpoint.Success(task).ToResult() : endpoint.Error(404).ToResult();

// One adapter around the whole switch or ?:
return (result switch
{
    Found found => endpoint.Success(found.Task),
    NotFound => endpoint.Error(404),
}).ToResult();

// A local holding the terminal (or a switch of them), adapted on the way out
var response = result switch { /* ... */ };
return response.ToResult();

// Lambda arms, such as a result type's Match, sync or async
return outcome.Match(
    found: task => endpoint.Success(task).ToResult(),
    missing: _ => endpoint.Error(404).ToResult());
```

A lambda counts when it's passed straight to the call the handler returns, such as `Match`
or `MatchAsync`, awaited or not, including a `Match` that one of those arms returns. Naming
the arm first changes nothing: a lambda in a local (`var found = (TaskDto t) => ...;`) or a
local function passed as `outcome.Match(found, ...)` is still the handler's own code. A lambda
buried in an argument doesn't count. In
`return Results.Ok(items.Select(t => endpoint.Success(t).ToResult()));` the response is
the `Ok`, not the terminal inside `Select`. Nor does a `Match` whose answer is thrown away.
In a minimal API the handler is the lambda you hand to `MapGet` and friends, not the `Match`
arm inside it, so that's whose route and parameters get checked.

Helpers still don't count towards coverage. A terminal handed to a helper to adapt, as in
`Done(endpoint.Success(task))`, or built inside one, as in `NotFound(endpoint)`, belongs to
the helper, not the handler. Both use the binding, though, so neither is an
`OrphanedBinding`.

## Exit code

When `--check` is used, any finding makes the command exit with code `1` — suitable for CI,
both with and without `--output`: coverage runs before emission, so a failed check prints
the findings and writes nothing even when an output directory is requested. Combine with
`-q`/`--quiet` to suppress generation output in CI logs.
