# Contract-driven sample

Demonstrates Rivet's contract-driven endpoint definitions using `[RivetContract]` with typed
`RouteDefinition<T>` fields. Contracts are pure Rivet (no ASP.NET dependency). Controllers use
`.Bind()` for input-bearing endpoints, execute ordinary application code, and construct
contract-owned responses with `.Success()`, `.Error()`, or `.File()`.

Features shown: `[RivetContract]`, `RouteDefinition<T>.Bind()`, `.Success()`, `.Description()`,
`.Returns<T>(status, desc)`, `.Status()`, `.Anonymous()`, `.Secure()`, branded value objects,
and the first-party `RivetResult.ToActionResult()` / `.ToResult()` framework bridges.

## Run

```bash
# Preview the OpenAPI 3.1 spec to stdout
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --security bearer

# Write openapi.json to disk
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --output /tmp/rivet-contract --security bearer

# Coverage check: every contract field has an implementation on the declared route/method
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --check

# Drift gate: exit 1 if the written spec no longer matches the compiled C#
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --output /tmp/rivet-contract --security bearer --verify
```

All commands should be run from the repository root.
