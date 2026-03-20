# How It Works

Rivet is a CLI tool — not a source generator — that uses Roslyn to analyse your C# project and emit TypeScript files.

## Why a CLI tool, not a source generator?

Source generators run inside the compiler and are limited to emitting C# code into the current compilation. Rivet needs to:

- Emit `.ts` files to an arbitrary output directory
- Optionally run `tsc` with the typia transformer
- Optionally emit `openapi.json`
- Work across project boundaries

A CLI tool (like `dotnet-ef` or `dotnet-format`) can do all of this. You run it when you want, and it produces files where you want them.

## Architecture

### Forward pipeline: C# → TypeScript

```
.csproj
  → MSBuildWorkspace (Roslyn)
  → Compilation
  → Walkers (ContractWalker, EndpointWalker, TypeWalker)
  → Model (TsEndpointDefinition, TsTypeDefinition)
  → Emitters (TypeEmitter, ClientEmitter, ValidatorEmitter, OpenApiEmitter)
  → .ts files + openapi.json
```

1. Opens your `.csproj` via Roslyn's `MSBuildWorkspace`
2. Discovers all types transitively from endpoint/contract signatures (plus any explicitly marked `[RivetType]`)
3. Finds `[RivetClient]` classes and `[RivetEndpoint]` methods — reads `[HttpGet]`, `[Route]`, `[ProducesResponseType]`, `[FromBody]`, etc.
4. Finds `[RivetContract]` classes and reads their `RouteDefinition<T>` chains via Roslyn's semantic model
5. Merges controller-sourced and contract-sourced endpoints (contract wins on collision)
6. Groups types by C# namespace, promotes cross-referenced types to `common.ts`
7. Emits per-controller client files and optionally `validators.ts` and `openapi.json`
8. With `--compile`, runs `tsc` with the typia transformer to produce runtime validators; with `--compile zod`, emits JSON Schema + Zod validators (no compile step)

### Reverse pipeline: OpenAPI → C#

```
openapi.json
  → OpenApiImporter (parser)
  → SchemaMapper (JSON Schema → C# types)
  → ContractBuilder (operations → RouteDefinition chains)
  → CSharpWriter
  → .cs files
```

1. Parses an OpenAPI 3.x JSON spec
2. Maps `#/components/schemas` to C# types (sealed records, enums, branded VOs)
3. Groups `#/paths` operations by tag into `[RivetContract]` static classes
4. Emits `.cs` files that feed directly into the forward pipeline

## Multi-SDK discovery

Roslyn's `MSBuildWorkspace` needs to find the .NET SDK. Rivet handles the common cases:

- System install (`/usr/local/share/dotnet`, `C:\Program Files\dotnet`)
- Homebrew on macOS (`/opt/homebrew/share/dotnet`)
- Linux package manager installs

The SDK path is resolved at startup. If Rivet can't find it, you'll get a clear error message.
