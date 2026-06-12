# Contract-driven sample

Demonstrates Rivet's contract-driven endpoint definitions using `[RivetContract]` with typed
`RouteDefinition<T>` fields. Contracts are pure Rivet (no ASP.NET dependency). Controllers use
`.Invoke()` for type-safe execution — the compiler enforces input/output types.

Features shown: `[RivetContract]`, `RouteDefinition<T>.Invoke()`, `.Description()`,
`.Returns<T>(status, desc)`, `.Status()`, `.Anonymous()`, `.Secure()`, branded value objects,
and `RivetResult<T>.ToActionResult()` as the framework bridge.

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
