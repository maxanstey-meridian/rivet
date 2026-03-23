# CLAUDE.md

## Project conventions (beyond global CLAUDE.md)

### Nuxt / Vue

- `<script setup lang="ts">` always.
- `useState()` with namespace keys for SSR-safe shared state. No Pinia.
- Components are presentational; side effects live in pages or composables.
- @nuxt/ui v4 for component library.

### .NET / C# (project-specific)

- Primary constructor DI for use cases. Signature: `ExecuteAsync(Command, CancellationToken)`.
- EF Core entities: `sealed class` (not records), `init`/`set`. Fluent config in `*EntityConfiguration.cs`.
- Module registration via `IServiceCollection` extension methods. No auto-scanning.
- FluentValidation with `.WithErrorCode()`. Custom `ValidationActionFilter`.
- Cookie-based auth (HttpOnly `sid`/`rtid`). JWT extracted from cookie.
- `JsonStringEnumConverter` globally.
- Colocate `Command` and `Result` records with their use case class.

## Rivet architecture

### Two pipelines

1. **C# → TS** (forward): Roslyn `[RivetContract]`/`[RivetClient]` → `ContractWalker`/`EndpointWalker` →
   `TsEndpointDefinition` + `TsTypeDefinition` → emitters (Type, Client, ZodValidator, OpenApi)
2. **OpenAPI → C#** (import): JSON → `OpenApiImporter` → `SchemaMapper` + `ContractBuilder` → `CSharpWriter` → `.cs`
   files that feed pipeline 1

### Contract style

`[RivetContract] public static class` with `RouteDefinition<T>` fields via `Define.Get/Post/etc.` factory methods. Not
abstract classes, not ASP.NET-coupled.

### Change ripple map

| If you change…                                   | Also verify…                                                           |
|--------------------------------------------------|------------------------------------------------------------------------|
| `Rivet.Attributes`                               | ContractWalker, EndpointWalker, CSharpWriter, samples build, all tests |
| Importer (`Rivet.Tool/Import/`)                  | Fixture round-trip tests, samples, drift detection tests               |
| `ContractWalker`/`EndpointWalker`                | OpenApiEmitterTests, ContractEndpointTests, importer round-trip        |
| OpenAPI emitter                                  | OpenApiEmitterTests; check type mapping consistency with importer      |
| Type mappings (SchemaMapper, TypeWalker, TsType) | Both pipeline directions; all emitters share `TsType` methods          |

### Testing

- Always add tests for new functionality.
- Fixture round-trip tests mandatory for importer changes.
- Run `dotnet test` (full suite) and `dotnet build samples/ContractApi/ContractApi.csproj` before done.

### Staleness hotspots

`Rivet.slnx` project refs, `samples/ContractApi/` contract style, README feature docs, importer test assertions.
