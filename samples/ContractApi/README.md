# Contract-driven sample

Demonstrates Rivet's contract-driven endpoint definitions using `[RivetContract]` with typed
`RouteDefinition<T>` fields. Contracts are pure Rivet (no ASP.NET dependency). Controllers use
`.Bind()` for input-bearing endpoints, execute ordinary application code, and construct
contract-owned responses with `.Success()`, `.Error()`, or `.File()`.

Features shown: `[RivetContract]`, `RouteDefinition<T>.Bind()`, `.Success()`, `.Description()`,
`.Returns<T>(status, desc)`, `.Status()`, `.Anonymous()`, `.Secure()`, `.QueryAuth()`, route
tokens typed by their constraint (`{id:guid}`), branded value objects, and the first-party
`RivetResult.ToActionResult()` / `.ToResult()` framework bridges.

The spec and the handlers tell the same story about security. `Contracts/Security.cs` declares
it in source, the way an app should: the `bearer` scheme, and bearer as the document's default
(`[assembly: RivetSecurityScheme]`, `RivetGlobalSecurity`, and `RivetGlobalSecurityScheme`). So
every endpoint needs a token except `Health`, which says `.Anonymous()`, and generation and
`--check` read the same default with no `--security` on either command line. The controller actions carry `[Authorize]`, and the health check is a minimal
API nobody guards. `Auth/DemoBearerHandler.cs` is the scheme behind it: it lets any non-empty
token through, from the `Authorization: Bearer` header or, for the `.QueryAuth()` avatar
endpoint, the `?token=` query parameter a media player can manage. It runs only in
Development, so a copied `Program.cs` refuses to start anywhere else rather than let every
token through. A real app registers its own scheme under the same name, and that scheme
has to read the query token for `.QueryAuth()` endpoints too: JwtBearer only reads the
`Authorization` header, so it needs a `JwtBearerEvents.OnMessageReceived` that sets
`context.Token` from `?token=`, or the avatar endpoint answers every player with a 401.
`--check` compares the two sides, so an `[Authorize]` that goes missing is `RIV4008`, not a
surprise in production.

## Run

```bash
# Preview the OpenAPI 3.1 spec to stdout
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj

# Write openapi.json to disk
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --output /tmp/rivet-contract

# Coverage check: every contract field has an implementation on the declared route and method,
# binds what the contract declares, and is authorized exactly where the spec asks for a token
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --check

# Drift gate: exit 1 if the written spec no longer matches the compiled C#
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --output /tmp/rivet-contract --verify

# Boot the API itself; Properties/launchSettings.json makes it Development, the only place it runs
dotnet run --project samples/ContractApi
```

All commands should be run from the repository root.
