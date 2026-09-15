# Azure Functions isolated worker

Use the isolated worker's **ASP.NET Core integration** with
`ConfigureFunctionsWebApplication()`, `HttpRequest`, and `IActionResult`. Return
Rivet terminals through the first-party `.ToActionResult()` adapter. No custom
Rivet bridge or separate Functions response framework is required.

The runnable `samples/FunctionsApi` project targets .NET 10 and Core Tools v4.
It serves two binary representations and a declared JSON error without Azure
credentials or storage. From the repository root:

```bash
dotnet build samples/FunctionsApi/FunctionsApi.csproj
cd samples/FunctionsApi
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated func start
# GET http://localhost:7071/api/images/png
# GET http://localhost:7071/api/images/jpeg
# GET http://localhost:7071/api/images/gif -> declared 404
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

## Check the implementation and the running host

```bash
dotnet run --project Rivet.Tool/Rivet.Tool.csproj -- \
  --project samples/FunctionsApi/FunctionsApi.csproj --check
```

Coverage recognizes `[Function]` plus `[HttpTrigger]` methods returning a contract
terminal through `.ToActionResult()`. It checks the HTTP method and effective
route, and rejects leading slashes in trigger routes. With a project input, it reads the sibling `host.json`; a missing prefix
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
checks emitted OpenAPI and coverage for default, empty and custom prefixes.
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
