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
| `--project` + `--compile` | Forward + runtime validators (typia or zod) |
| `--project` + `--openapi` | Forward + OpenAPI 3.0 JSON spec |
| `--project` + `--check` | Verify contract coverage against implementations |
| `--project` + `--routes` | List all discovered endpoints |
| `--from-openapi` | Reverse: OpenAPI → C# contracts + DTOs |

## Flags

| Flag | Alias | Description |
|---|---|---|
| `--project <path>` | | Path to the `.csproj` file to analyse |
| `--output <dir>` | `-o` | Output directory. Omit to preview to stdout |
| `--compile [typia\|zod]` | | Compile validators — typia (default, requires Node.js) or zod (`fromJSONSchema`). Requires `--output` |
| `--jsonschema` | | Emit standalone JSON Schema definitions (`schemas.ts`) |
| `--openapi` | | Emit OpenAPI 3.0 JSON alongside TS output |
| `--security <scheme>` | | Security scheme for OpenAPI spec |
| `--check` | | Verify contract coverage (missing implementations, route/method mismatches) |
| `--routes` | | List all discovered endpoints (method, route, handler) |
| `--quiet` | `-q` | Suppress codegen preview output (useful with `--check`) |
| `--from-openapi <path>` | | Path to OpenAPI 3.x JSON spec to import |
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
dotnet rivet --project path/to/Api.csproj --output ./generated

# Generate with runtime validators (typia, default)
dotnet rivet --project path/to/Api.csproj --output ./generated --compile

# Generate with Zod validators (no Node compile step)
dotnet rivet --project path/to/Api.csproj --output ./generated --compile zod

# Generate standalone JSON Schema definitions
dotnet rivet --project path/to/Api.csproj --output ./generated --jsonschema

# Generate with OpenAPI spec
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi

# Generate with OpenAPI spec + security
dotnet rivet --project path/to/Api.csproj --output ./generated --openapi --security bearer
```

### Contract coverage check

```bash
# Check that all contract endpoints have matching implementations
dotnet rivet --project path/to/Api.csproj --check --quiet

# Check with codegen preview
dotnet rivet --project path/to/Api.csproj --check

# Check + generate (both run together)
dotnet rivet --project path/to/Api.csproj --output ./generated --check
```

Reports missing implementations, HTTP method mismatches, and route mismatches. Prints a coverage summary (e.g. `Coverage: 79/79 endpoints covered. All OK.`). Exits with code 1 if any warnings are found (standalone mode without `--output`).

### Route list

```bash
# List all endpoints (like php artisan route:list)
dotnet rivet --project path/to/Api.csproj --routes
```

Output:

```
  Method  Route                   Handler
  ──────  ──────────────────────  ───────
  GET     /api/health             members.health
  GET     /api/members            members.list
  POST    /api/members            members.invite
  DELETE  /api/members/{id}       members.remove
  PUT     /api/members/{id}/role  members.updateRole
5 route(s).
```

### Reverse pipeline (OpenAPI import)

```bash
# Preview imported contracts
dotnet rivet --from-openapi spec.json --namespace TaskBoard.Contracts

# Write to disk
dotnet rivet --from-openapi spec.json --namespace TaskBoard.Contracts --output ./src/

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
