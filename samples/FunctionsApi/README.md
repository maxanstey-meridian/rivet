# Functions contract sample

.NET 10 isolated Azure Functions with ASP.NET integration, contract-owned file
responses and a declared JSON error. No Azure account or external storage needed.

From the repository root:

```sh
dotnet build samples/FunctionsApi/FunctionsApi.csproj
cd samples/FunctionsApi
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated func start
```

Request `/api/images/png`, `/api/images/jpeg`, or `/api/images/gif` (404).
The small byte payloads are transport fixtures, not complete displayable images.

Run `task test:functions` at the root for the real-host regression gate. It uses
disposable copies and checks default, empty and custom prefixes, response bytes,
MIME selection, errors, methods, OpenAPI emission and implementation coverage.

See [the Functions guide](../../docs/guides/azure-functions.md) for route conventions,
configuration and coverage limitations. This sample references Rivet's source
project and exercises features not yet included in the published 0.41.0 package.
