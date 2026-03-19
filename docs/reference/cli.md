# CLI Reference

## Installation

```bash
dotnet tool install --global dotnet-rivet
```

## Modes

Rivet detects which mode to run based on the flags provided:

| Flags | Mode |
|---|---|
| `--project` | Forward: C# → TypeScript |
| `--project` + `--compile` | Forward + typia runtime validators |
| `--project` + `--openapi` | Forward + OpenAPI 3.1 JSON spec |
| `--project` + `--check` | Verify contract coverage against implementations |
| `--from-openapi` | Reverse: OpenAPI → C# contracts + DTOs |

## Flags

| Flag | Alias | Description |
|---|---|---|
| `--project <path>` | | Path to the `.csproj` file to analyse |
| `--output <dir>` | `-o` | Output directory. Omit to preview to stdout |
| `--compile` | | Compile typia validators (requires Node.js on PATH) |
| `--openapi` | | Emit OpenAPI 3.1 JSON alongside TS output |
| `--security <scheme>` | | Security scheme for OpenAPI spec |
| `--check` | | Verify contract coverage (missing implementations, route/method mismatches) |
| `--from-openapi <path>` | | Path to OpenAPI 3.1 JSON spec to import |
| `--namespace <ns>` | | C# namespace for imported contracts/types |

## Security scheme formats

The `--security` flag accepts these formats:

| Format | OpenAPI scheme |
|---|---|
| `bearer` | HTTP Bearer |
| `bearer:jwt` | HTTP Bearer with JWT format |
| `cookie:<name>` | API key in cookie (e.g., `cookie:sid`) |
| `apikey:header:<name>` | API key in header (e.g., `apikey:header:X-Api-Key`) |

## Preview mode

Omit `--output` on any command to preview generated output to stdout without writing files:

```bash
# Preview TS output
dotnet rivet --project Api.csproj

# Preview imported C# contracts
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts
```

## Examples

### Forward pipeline

```bash
# Generate TS types + client
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet

# Generate with runtime validators
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --compile

# Generate with OpenAPI spec
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --openapi

# Generate with OpenAPI spec + security
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --openapi --security bearer
```

### Contract coverage check

```bash
# Check that all contract endpoints have matching implementations
dotnet rivet --project path/to/Api.csproj --check

# Check + generate (both run together)
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --check
```

Reports missing implementations, HTTP method mismatches, and route mismatches. Exits with code 1 if any warnings are found (standalone mode without `--output`).

### Reverse pipeline (OpenAPI import)

```bash
# Preview imported contracts
dotnet rivet --from-openapi spec.json --namespace TaskBoard.Contracts

# Write to disk
dotnet rivet --from-openapi spec.json --namespace TaskBoard.Contracts --output ./src/Contracts/

# With security scheme
dotnet rivet --from-openapi spec.json --namespace TaskBoard.Contracts --output ./src/ --security bearer
```

### Development (from source)

When running from the repo instead of the installed tool:

```bash
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj --output /tmp/rivet-output
dotnet run --project Rivet.Tool -- --project samples/AnnotationApi/AnnotationApi.csproj --output /tmp/rivet-output --compile
```
