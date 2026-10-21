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
`RouteMismatch`.

`--check` reports five kinds of finding on stderr. The first four are warnings; `UnbindableEnumParameter` is an error (`RIV4005`):

| Warning | Meaning |
|---|---|
| `MissingImplementation` | No contract response terminal found for the contract field. |
| `OrphanedBinding` | The contract was bound in a route handler, but no terminal result from that binding is returned. |
| `HttpMethodMismatch` | The implementing endpoint uses a different HTTP method than the contract declares. |
| `RouteMismatch` | The implementing endpoint's route does not match the contract's route template. |
| `UnbindableEnumParameter` | The handler binds an enum from the route, query, header or form (directly, or as a member of an `[AsParameters]` or explicitly `[FromQuery]`-style complex parameter), and its host would refuse one of the enum's advertised wire values. See [below](#enum-parameters-the-host-can-t-bind). |

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
parses it itself are fine everywhere. Azure Functions handlers aren't checked. The
full story, including what the hosts accept beyond the spec, is in
[Limitations](/misc/limitations#enum-parameters-are-bound-by-asp-net).

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
it's answering for, because the bound endpoint carries its own contract. The one thing a
helper can't do is hold a handler's *only* terminal. A handler that never returns one of
its own isn't tied to any field, so if nothing else implements that field, `--check`
reports it as `MissingImplementation`.

## Exit code

When `--check` is used, any warning makes the command exit with code `1` — suitable for CI,
both with and without `--output`: coverage runs before emission, so a failed check prints
the warnings and writes nothing even when an output directory is requested. Combine with
`-q`/`--quiet` to suppress generation output in CI logs.
