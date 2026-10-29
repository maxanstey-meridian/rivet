# Functions contract sample

.NET 10 isolated Azure Functions with ASP.NET integration, contract-owned file
responses, a JSON endpoint with an enum route value and an enum query parameter,
and a declared JSON error. No Azure account or external storage needed.

From the repository root:

```sh
dotnet build samples/FunctionsApi/FunctionsApi.csproj
cd samples/FunctionsApi
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated func start
```

Request `/api/images/png`, `/api/images/jpeg`, or `/api/images/gif` (404), and
`/api/images/png/thumbnails/1?fit=0` for a thumbnail description. The sizes and
fits are plain enums, integers on the wire, because the worker reads a route or
query value as JSON.
The small byte payloads are transport fixtures, not complete displayable images.

Run `task test:functions` at the root for the real-host regression gate. It uses
disposable copies and checks default, empty and custom prefixes, response bytes,
MIME selection, errors, methods, OpenAPI emission, implementation coverage, and
what the worker binds into a function's parameters.

See [the Functions guide](../../docs/guides/azure-functions.md) for route conventions,
configuration and coverage limitations. This sample references Rivet's source
projects, so it can use features newer than the latest published package.
