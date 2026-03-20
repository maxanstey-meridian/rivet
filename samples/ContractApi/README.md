# Contract-driven sample

Demonstrates Rivet's contract-driven endpoint definitions using `[RivetContract]` with typed
`RouteDefinition<T>` fields. Contracts are pure Rivet (no ASP.NET dependency). Controllers use
`.Invoke()` for type-safe execution — the compiler enforces input/output types.

Features shown: `[RivetContract]`, `RouteDefinition<T>.Invoke()`, `.Description()`,
`.Returns<T>(status, desc)`, `.Status()`, `.Anonymous()`, `.Secure()`, branded value objects,
and `RivetResult<T>.ToActionResult()` as the framework bridge.

## Run

```bash
# Preview to stdout
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj

# Preview with OpenAPI spec
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --openapi --security bearer

# Write to disk
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --output /tmp/rivet-contract

# Write to disk with OpenAPI spec
dotnet run --project Rivet.Tool -- --project samples/ContractApi/ContractApi.csproj --output /tmp/rivet-contract --openapi --security bearer
```

All commands should be run from the repository root.
