# Contract-driven sample

Demonstrates Rivet's contract-driven endpoint definitions using `[RivetContract]` — declarative endpoint
shapes that Rivet reads at generation time, with nothing executing at runtime.

Features shown: `[RivetContract]`, `.Description()`, `.Returns<T>(status, desc)`, `.Status()`,
`.Anonymous()`, `.Secure()`, branded value objects, and `[RivetImplements]` to link controllers to contracts.

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
