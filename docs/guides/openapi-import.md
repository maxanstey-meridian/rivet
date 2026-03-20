# OpenAPI Import

Start from an OpenAPI 3.x JSON spec, generate C# contracts and DTOs, then feed them into the normal Rivet pipeline to produce TypeScript output.

```
OpenAPI spec (source of truth)
  → C# contracts + DTOs (generated, checked in)
  → Roslyn walker (existing)
  → TS types + client (existing)
```

This is useful when another team owns the API — import their spec, get typed contracts, and the compiler tells you what broke when the upstream spec changes. Re-run the import, rebuild, fix what the compiler flags.

## Command

```bash
# Preview to stdout
dotnet rivet --from-openapi openapi.json --namespace TaskBoard.Contracts

# Write to disk
dotnet rivet --from-openapi openapi.json --namespace TaskBoard.Contracts --output ./src/

# With default security scheme
dotnet rivet --from-openapi openapi.json --namespace TaskBoard.Contracts --output ./src/ --security bearer
```

## Output structure

```
output/
├── Types/
│   ├── TaskDto.cs              # sealed record
│   ├── CreateTaskRequest.cs
│   └── Priority.cs             # enum
├── Domain/
│   └── Email.cs                # branded value object
└── Contracts/
    ├── TasksContract.cs        # [RivetContract] with RouteDefinition<T> fields
    └── MembersContract.cs
```

### What it generates

- **Sealed records** for object schemas (no `[RivetType]` needed — they're discovered transitively from the generated contracts)
- **Enums** for string enums
- **Branded value objects** for string types with semantic formats (`email`, `uri`, etc.)
- **Static contract classes** (v1 style) with `RouteDefinition<T>` builder chains
- Operations grouped by tag into separate contract classes

## Supported schema subset

See the [Type Mapping reference](/reference/type-mapping#openapi--c-import-direction) for the full mapping table.

## Unsupported features

The following produce a warning and are skipped:

- `discriminator` mappings
- XML-specific attributes
- Callbacks, webhooks, links

## Security scheme handling

If the source spec defines `securityDefinitions` / `components/securitySchemes`, the importer maps them to `.Secure()` / `.Anonymous()` calls on the generated contract endpoints.

You can also override with `--security` on the command line.

## Using the generated contracts

The generated C# files compile immediately and work with the existing Rivet pipeline. Feed them back into the forward pipeline:

```bash
# Step 1: Import OpenAPI → C#
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/

# Step 2: Generate TS from the imported contracts
dotnet rivet --project MyApp.csproj --output ./generated
```

The compiler enforces that your implementations match the imported contracts. When the upstream spec changes, re-run the import and fix what the compiler flags.
