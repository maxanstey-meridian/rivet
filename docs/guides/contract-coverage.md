# Contract Coverage

`--check` verifies that every `[RivetContract]` field is actually implemented, and
that the implementation matches the declaration:

```bash
dotnet rivet --project path/to/Api.csproj --check
```

The checker finds `.Success(...)`, `.Error(...)`, and `.File(...)` terminal calls for
each contract field, including terminals reached through `.Bind(input)`, in
controller actions, minimal API handlers (whatever you hand `MapGet`/`MapPost`/
`MapPut`/`MapDelete`/`MapPatch`: a lambda, a local holding one, a local function, or
a method group), and isolated Azure Functions using
`[Function]`, `[HttpTrigger]`, and `.ToActionResult()`. Functions coverage reads
the project's `host.json` HTTP prefix (default `api`) and honors the host
environment override. See [Azure Functions](./azure-functions) for conventions
and a real-host test.

A `Map*` route can be a string constant or the contract field's own `.Route`, as in
`app.MapGet(MembersContract.Health.Route, …)`, and `MapGroup` prefixes count either
way: the contract owns the full path, so a group prefix it doesn't declare is a
`RouteMismatch`. A convention on the group doesn't lose it, so
`var api = app.MapGroup("/api").RequireAuthorization();` still prefixes `api.MapGet(…)`. Anything else (a variable, concatenation, interpolation, or a
`MapGroup` prefix that isn't a constant) is reported as an unresolved
`RouteMismatch`, because `--check` won't guess a route.

`--check` reports eight kinds of finding on stderr. The first four are warnings; `UnbindableEnumParameter` (`RIV4005`), `InputBindingMismatch` (`RIV4006`), `SecurityMismatch` (`RIV4008`), and `AuthorizationUnreadable` (`RIV4009`) are errors:

| Finding | Meaning |
|---|---|
| `MissingImplementation` | No contract response terminal found for the contract field. |
| `OrphanedBinding` | The contract was bound in a route handler, but the handler neither returns a terminal built from that binding nor passes the binding to a helper whose answer it returns. |
| `HttpMethodMismatch` | The implementing endpoint uses a different HTTP method than the contract declares. |
| `RouteMismatch` | The implementing endpoint's route does not match the contract's route template. |
| `UnbindableEnumParameter` | The handler binds an enum from the route, query, header or form (directly, or as a member of a complex parameter the host binds member by member), and its host would refuse one of the enum's advertised wire values. See [below](#enum-parameters-the-host-can-t-bind). |
| `InputBindingMismatch` | The handler binds an input the contract doesn't declare, never binds one it does, or binds it from somewhere else. See [below](#inputs-the-handler-doesn-t-bind-where-the-contract-says). |
| `SecurityMismatch` | The spec says credentials are needed and anyone can call the handler, or the spec lets anonymous callers in and the handler turns them away. See [below](#security-the-handler-enforces). |
| `AuthorizationUnreadable` | Whether the handler requires authentication depends on setup `--check` can't read statically, such as a requirement applied only under an `if`. See [below](#security-the-handler-enforces). |

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
a route or query value as JSON, so no name binds at all, and only a plain enum or a
`string` will do. The full story, including what the hosts accept beyond the spec, is in
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
values, so each one counts as an input of its own, under the name the spec gives it. The
spec names a form field as both hosts' binders read the contract's own record (see [Part
names](file-uploads.md#part-names)), so a handler binding that record is clean. A handler
binding some other record, whose `Body` property the binder reads as `Body` where the spec
says `note_body`, reports both halves, just like the query example above.

Names compare ignoring case, as ASP.NET's do, and the check follows the same binding rules
as `RIV4005`: explicit `[From*]` attributes and their `Name =`, `[AsParameters]`, and each
host's inference. On MVC, a `Name =` on a complex parameter is the prefix its members are
read under, so `[FromQuery(Name = "f")] NoteSearch filter` wants `?f.Search=`, which no
client sends. Minimal APIs read a `StringValues` like a `string[]`, from the query on a GET,
and never consult a `[TypeConverter]`, so a type that only has one is a service or a body
there. And because every MVC answer depends on whether `[ApiController]` infers binding
sources, a `SuppressInferBindingSourcesForParameters` set to anything but a constant stops
the check with `RIV1115` instead of letting it guess. Members MVC reads under a prefix keep
it when renamed (`?f.q=`), and header members never take one. `[Bind(Prefix = "f")]`, on
the parameter or on its class, sets the same prefix, and a member's `[BindProperty(Name =
...)]` renames it like a `[FromQuery(Name = ...)]` would.

A minimal API's `[FromForm]` record plays by different rules, because it goes through the
form mapper rather than model binding. The mapper reads every member from the form under
its C# name, nested ones as `Inner.Value`, and the only rename it honours is
`[DataMember(Name = ...)]`. It never prefixes, so a `Name =` on the parameter changes
nothing, and it ignores the binding attributes on the members: a member's
`[FromForm(Name = "note_body")]` still reads `Body`, and a `[FromQuery]` member is still a
form field. `--check` reads the record the way the mapper does, so the `[FromForm(Name =
...)]` that fixes an MVC action is reported on a minimal API, and the fix there is the
`[DataMember]`.

A handler that can read the request itself is trusted to pick inputs out by hand: an
`HttpContext` or `HttpRequest` parameter, a `BindAsync` type, a custom model binder, or a
handler body that reads the request or route data through a property (a controller's
`Request` or `RouteData`, `ControllerContext.HttpContext.Request`, an
`IHttpContextAccessor`'s `HttpContext.Request`), calls `HttpContext.GetRouteValue`, or
hands the `HttpContext` or `HttpRequest` to another method (`HttpContext.GetTenantId()`).
Touching only `HttpContext.User`, `Response` or `RequestAborted` doesn't count. A method
or property the handler calls on `this`, its own class's or a base's, is read too, as deep
as the calls go, so a controller's private `RouteId()` that reads `Request.RouteValues`
counts. A helper anywhere else isn't followed: it reaches the request through an argument,
and handing it the `HttpContext` already counts. Such a handler is never told an input is
unbound, though what it does bind is still checked. That's also the only way to read a cookie the contract
declares: ASP.NET has no attribute that binds one, so `--check` reports a declared cookie
unless the handler can read `Request.Cookies`. An unattributed complex parameter that
could be a service counts as the body when the contract has one and as a service when it
doesn't. The `Authorization` header, a `.QueryAuth()` token and the header or query
parameter of an apiKey scheme the endpoint's security names (its own `.Secure(...)`, or
the document's global security, from attributes or `--security`) belong to the contract's
security, not its inputs; `--check` takes the same `--security` values as generation.

An Azure Functions trigger hands the function the whole request, so a function is never
told an input is unbound. Its other parameters are compared, though, because the worker
fills each one by its name, ignoring case: from a route value first, then a query
parameter, then a top-level property of a JSON body. It never looks at headers, cookies or
form fields, and a name nothing carries is `null`, or, for a value type, a failed call. So
a misspelt route token is caught:

```csharp
// The trigger's route is images/{format}/thumbnails/{size}. The worker looks sz up in the
// query, where no client sends it, so every call fails before the function runs.
public IActionResult Run([HttpTrigger(…, Route = ImagesContract.ThumbnailTriggerRoute)] HttpRequest _,
    string format, ThumbnailSize sz, ThumbnailFit fit) => …
```

That's `RIV4006`: the handler binds a query parameter `sz` the contract doesn't declare. A
parameter named after a declared cookie, header or form field is reported as the query
parameter it really is, and the fix is to read the value from the request. A parameter
named after one of a JSON body's top-level properties is fine, since that's the worker's
third place to look. [Azure Functions](./azure-functions) has the full binding rules,
pinned against the real host.

## Security the handler enforces

A contract's `.Secure(...)` is a promise about the wire: it tells every generated client to
send credentials. Nothing in OpenAPI enforces it, though. That's the handler's job, and a
spec that says "credentials needed" over a handler anyone can call is the most dangerous
way for a spec to be looser than the truth. So `--check` compares the two.

It asks one question only: does a caller have to authenticate at all? Schemes and policies
can't be matched (`[Authorize(Policy = "Staff")]` says nothing about bearer tokens), so
`--check` doesn't try. Two disagreements are `RIV4008`:

- The spec has no anonymous alternative, but the handler is reachable anonymously.
- The spec admits anonymous callers (`.Anonymous()`, `.OrAnonymous()`, or no security at
  all), but the handler requires authentication, so those callers get a 401.

The spec's security is the endpoint's own, else the document's default. A `[RivetEndpoint]`
action is compared too, with its `[RivetSecure]`, `[RivetOrAnonymous]`, and
`[RivetAnonymous]` attributes as its spec (an action that also implements a contract field is
compared once, as the field's handler). That catches `[AllowAnonymous]` sitting beside a
`[RivetSecure]` that has no anonymous alternative.

### No security declared means anyone may call

When neither the endpoint nor the document says anything about security, the emitted
OpenAPI has no `security` at all, which OpenAPI reads as "no credentials needed". `--check`
reads it the same way, so a handler that requires authentication under such a contract is
a disagreement.

If the document declares no security scheme at all, those handlers have one cause, and you
get one `RIV4008` naming every endpoint instead of a page of them. The fix is to declare the
document's default in source:

```csharp
[assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer")]
[assembly: RivetGlobalSecurity(0)]
[assembly: RivetGlobalSecurityScheme(0, "bearer", new string[0])]
```

Then mark the endpoints anyone may call `.Anonymous()`. Declare it in source rather than
only with `--security`: generation and `--check` are separate runs, and a check run without
the generation flags would compare against a different spec from the one you ship. See
[Attributes](/reference/attributes#security) for the assembly attributes.

### What counts as authorization

`--check` reads ASP.NET's own authorization metadata, and nothing else:

- `IAuthorizeData` and `IAllowAnonymous` attributes (`[Authorize]`, `[AllowAnonymous]`, and
  your own attributes implementing them) on the action, its controller, and base
  controllers, or on a minimal-API handler (the lambda or the method a method group names);
- an `AuthorizeFilter` added to `MvcOptions.Filters`, as an MVC-wide requirement that
  `[AllowAnonymous]` opts out of;
- `RequireAuthorization()` and `AllowAnonymous()` on the endpoint, on any `MapGroup` it's
  mapped in, and on `MapControllers()`;
- the fallback policy, set by `AddAuthorization(o => o.FallbackPolicy = ...)` or
  `AddAuthorizationBuilder().SetFallbackPolicy(...)`, which applies only to an endpoint with
  no authorization of its own;
- for Azure Functions, the trigger's `AuthorizationLevel`. `Anonymous` lets anyone in; any
  other level needs a key, and leaving the level out means the attribute's default,
  `Function`.

It weighs them the way ASP.NET does. An `[AllowAnonymous]` anywhere wins, over the
controller's `[Authorize]`, the global filter, `RequireAuthorization()`, and the fallback
alike. So the usual MVC shape reads exactly as it runs:

```csharp
builder.Services.AddControllers(options => options.Filters.Add(new AuthorizeFilter()));
```

Every action requires an authenticated user, apart from the ones (or the controllers) marked
`[AllowAnonymous]`.

A policy counts as requiring authentication when it's a policy name (a constant), an
`[Authorize]`-style `IAuthorizeData`, or built inline from `AuthorizationPolicyBuilder`'s
user requirements (`RequireAuthenticatedUser`, `RequireRole`, `RequireClaim`,
`RequireUserName`), held in a local or not, as in
`new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())` or
`.RequireAuthorization(policy => policy.RequireRole("Staff"))`. A requirement it can't read
(`RequireAssertion`, a policy from a parameter, a field, or a method call) makes the verdict
unknown rather than guessed.

`--check` follows a builder the way it follows a route: through a local, through convention
calls that hand the builder back (`.WithName(...)`, `.RequireAuthorization()`), through a
helper that returns one, and into a helper you pass it to, at each call site. A convention on
a builder it can't trace to its `Map*` call (a lambda's parameter, a field, or a method handed
on as a delegate) may cover any endpoint on that host, so those endpoints become `RIV4009`.

### What it can't see

Custom authorization filters and middleware are invisible. An `IAsyncAuthorizationFilter`
that checks antiforgery tokens, or one that answers 403 to non-admins, doesn't change
anything `--check` reports, and it never makes an endpoint unreadable either: `--check`
can't tell what your code decides, so it doesn't pretend to. The same goes for
`IControllerModelConvention`s that add filters, and for authorization done by hand inside a
handler.

### When it can't decide

Some setup can't be read statically. A requirement applied only under a condition is the
common one:

```csharp
var controllerEndpoints = app.MapControllers();
if (entraEnabled)
{
    controllerEndpoints.RequireAuthorization();
}
```

Whether those actions need authentication depends on configuration `--check` never sees,
so each one is `RIV4009`, naming the site and the condition. An `if`, an `else`, a `?:`
branch, a `switch`, a loop, a `catch`, the right of `&&`, `||`, or `??`, a `?.`, and a
`return` that may leave before the call all count. A condition around both the endpoint and
its requirement doesn't: the requirement holds whenever the endpoint exists.

It's never a guess, though, and it isn't blanket: `--check` works the verdict out for every
way the unknowns could fall, and reports only when they disagree. An `[AllowAnonymous]`
action under that conditional `RequireAuthorization()` is anonymous either way, and an
`[Authorize]` one requires authentication either way, so neither is `RIV4009`. Neither is
a conditional requirement on an endpoint that the fallback policy would secure anyway.

The cure is to apply authorization unconditionally. If a development environment really
does run without authentication, give it a development authentication handler that signs
the dev user in, rather than switching authorization off. Then authorization is exercised
everywhere the app runs, and `--check` can read it.

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

Using the binding and covering the field are different things, though. `--check` gives a
field credit only for a terminal that reaches `.ToResult()` or `.ToActionResult()` on the
handler's way out. The handler can apply the adapter itself, or hand the terminal to a
helper that does (see [below](#how-a-handler-can-return-its-terminal)). A terminal a helper
builds for itself, like the `NotFound(endpoint)` above, belongs to the helper: it serves
any endpoint it's handed, so it can't speak for one. A handler whose every answer is built
inside helpers isn't tied to any field, and if nothing else implements that field,
`--check` reports it as `MissingImplementation`, binding or no binding.

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
In a minimal API the handler is whatever you hand `MapGet` and friends, not the `Match` arm
inside it, so that's whose route and parameters get checked. It doesn't have to be written
in place:

```csharp
app.MapGet(TasksContract.GetTask.Route, TaskHandlers.Get);   // a method group

public static class TaskHandlers
{
    public static async Task<IResult> Get(Guid id, TaskStore tasks)
    {
        var endpoint = TasksContract.GetTask.Bind(new TaskLookup(id));
        return await tasks.Find(id) is { } task
            ? endpoint.Success(task).ToResult()
            : NotFound(endpoint).ToResult();
    }
}
```

The method's parameters are what ASP.NET binds, so they're what `RIV4006` compares, exactly
as a lambda's would be. The same goes for a local function, or a local holding a lambda,
handed over by name. Map one method twice and each mapping is checked on its own verb and
route, so the one the contract doesn't declare is a `RouteMismatch`. A method group naming
something with no source in the project (`new object().ToString`, say) has no body to
read, so it implements nothing.

A helper can do the adapting, too. Handing the terminal to a helper counts when the helper
returns the adapter applied to the parameter the terminal lands in:

```csharp
return Done(endpoint.Success(task));

static IResult Done(RivetResult result) => result.ToResult();
```

`--check` follows any helper whose source is in the project, local functions and extension
methods included, through as many helpers as hand the terminal on, and reads each one once,
so a recursive helper can't send it round in circles. A helper that throws the terminal
away, or adapts a different argument, gives nothing: the field still needs a terminal from
somewhere else. Either way, the helper uses the binding, so it isn't an `OrphanedBinding`.

## Exit code

When `--check` is used, any finding makes the command exit with code `1` — suitable for CI,
both with and without `--output`: coverage runs before emission, so a failed check prints
the findings and writes nothing even when an output directory is requested. Combine with
`-q`/`--quiet` to suppress generation output in CI logs.
