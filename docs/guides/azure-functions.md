# Azure Functions isolated worker

Use the isolated worker's **ASP.NET Core integration** with
`ConfigureFunctionsWebApplication()`, `HttpRequest`, and `IActionResult`. Return
Rivet terminals through the first-party `.ToActionResult()` adapter. No custom
Rivet bridge or separate Functions response framework is required.

The runnable `samples/FunctionsApi` project targets .NET 10 and Core Tools v4.
It serves two binary representations, a JSON thumbnail description whose inputs
include an enum route value and an enum query parameter, and a declared JSON error,
without Azure credentials or storage. From the repository root:

```bash
dotnet build samples/FunctionsApi/FunctionsApi.csproj
cd samples/FunctionsApi
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated func start
# GET http://localhost:7071/api/images/png
# GET http://localhost:7071/api/images/jpeg
# GET http://localhost:7071/api/images/gif -> declared 404
# GET http://localhost:7071/api/images/png/thumbnails/1?fit=0
```

## Routes have two representations

A Functions trigger route is relative, with **no leading slash**. The OpenAPI
route is the complete public path, including the configured HTTP prefix and a
leading slash. Own both constants in the contract:

```csharp
public const string TriggerRoute = "images/{format}";
public const string Route = "/api/" + TriggerRoute;
```

Use `TriggerRoute` in `[HttpTrigger(..., Route = ImagesContract.TriggerRoute)]`
and `Route` in `Define.File<ImageInput>(Route)`. Do not pass the complete OpenAPI
path straight into the trigger attribute. With ASP.NET integration, a leading
slash can produce incorrect host routing even when spec generation succeeds.

The default prefix is `api`. To remove it:

```json
{
  "version": "2.0",
  "extensions": { "http": { "routePrefix": "" } }
}
```

Then the contract path is `"/" + TriggerRoute`. For `routePrefix: "public/v1"`,
it is `"/public/v1/" + TriggerRoute`. Keep the contract and deployment
configuration aligned. If `Route` is omitted, Functions uses the function name.

## Parameters the worker fills by name

A Function's trigger parameter is the whole `HttpRequest`. Any other parameter without a
binding attribute is filled by the isolated worker from the invocation's binding data,
looked up by the parameter's name, ignoring case. For an HTTP trigger the Functions host
puts three things there, and the first one carrying the name wins:

1. the route values of the trigger's route;
2. the query parameters;
3. the top-level properties of a request body that parses as a JSON object, whatever
   its content type says.

Headers, cookies and form fields never get there. A name nothing carries leaves a
reference type `null`, and fails the call outright for a value type. So in the sample's
thumbnail function, `format` and `size` come from `images/{format}/thumbnails/{size}`
and `fit` from `?fit=`, and a parameter called `sz` would be looked up in a query no
client fills. `--check` compares these names with the contract's inputs, a JSON body's
top-level properties included (`RIV4006`), so that typo fails the gate rather than every
request. A parameter for a declared header,
cookie or form field is reported too; read those from the request instead. Since the
trigger hands over the whole request, a function is never told it leaves a declared
input unbound: it may read it by hand.

Every value the worker fills goes through its input converters, and none of them turns
text into an enum except by parsing it as JSON. An integer is JSON; an unquoted member
name isn't, so `/thumbnails/Large` or `?fit=Contain` fails before the function runs. Bind
a route or query enum as a plain enum, integers on the wire as the sample's
`ThumbnailSize` and `ThumbnailFit` are, or as a `string` you parse yourself. `--check`
refuses a string enum there (`RIV4005`), whatever its converter.

## Check the implementation and the running host

```bash
dotnet run --project Rivet.Tool/Rivet.Tool.csproj -- \
  --project samples/FunctionsApi/FunctionsApi.csproj --check
```

Coverage recognizes `[Function]` plus `[HttpTrigger]` methods returning a contract
terminal through `.ToActionResult()`. It checks the HTTP method and effective
route, the parameters the worker fills by name (above), and rejects leading
slashes in trigger routes. It also holds the trigger's `AuthorizationLevel` to the
contract's security (`RIV4008`): `Anonymous` means anyone can call, so a `.Secure(...)`
contract over it is an error, and any other level needs a function key, so a contract that
lets anonymous callers in (or says nothing, which OpenAPI reads the same way) is one too.
Leave the level out and you get the attribute's default, `Function`, not `Anonymous`. Core
Tools doesn't enforce keys when it runs locally; `--check` reads what the trigger means once
deployed. See [Security the handler enforces](./contract-coverage#security-the-handler-enforces). With a project input, it reads the sibling `host.json`; a missing prefix
uses `api`, while an empty prefix stays empty. The environment variable
`AzureFunctionsJobHost__extensions__http__routePrefix` takes precedence, matching
the Functions host override convention. Invalid configuration fails the check.
Raw source-file analysis has no project host file and uses the default prefix
unless that environment override is set.

`--check` without `--output` is the failing coverage gate. `--verify` checks
**generated-spec drift**, not routing. Neither runs the host or discovers remote
deployment settings. Use the same configuration when checking and deploying.

From this source checkout, run:

```bash
task test:functions
```

This launches Core Tools against disposable sample builds and checks actual HTTP
bytes, content types, a declared JSON error, and HTTP method rejection. It also
checks emitted OpenAPI and coverage for default, empty and custom prefixes. A
second run adds probe functions to pin the binding rules above on the real host:
which sources fill a parameter and in what order, case, `null`s, and the enum
conversion, and that `--check` catches a parameter named after no route token.
The test requires .NET 10, Core Tools v4, and Python 3; missing prerequisites fail
with an actionable message. It does not require Docker or an Azure subscription.

## Select a declared binary representation

```csharp
var endpoint = ImagesContract.Download.Bind(new ImageInput(format));
return endpoint.File(bytes, contentType: actualMimeType).ToActionResult();
```

Declare every permitted MIME type on the contract. Rivet rejects an undeclared
selection, and rejects an omitted selection when more than one binary success
representation exists. Selection does not inspect or convert the file bytes.

The MIME selector and configurable-prefix coverage are additions in this source
checkout; use packages built from this revision until a release includes them.
