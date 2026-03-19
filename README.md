<p align="center">
  <img src="logo.png" alt="Rivet" width="200" />
  <h1 align="center">Rivet</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Rivet.Attributes"><img src="https://img.shields.io/nuget/v/Rivet.Attributes?label=Rivet.Attributes" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/dotnet-rivet"><img src="https://img.shields.io/nuget/v/dotnet-rivet?label=dotnet-rivet" alt="NuGet" /></a>
    <a href="https://github.com/maxanstey-meridian/rivet/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="License" /></a>
  </p>
</p>

End-to-end type safety between .NET and TypeScript. No drift, no schema files, no codegen config.

| Source of truth | Command | What it produces |
|---|---|---|
| **C# contracts** | `dotnet rivet --project Api.csproj` | TS types, typed client, validators, OpenAPI spec |
| **C# controllers** | `dotnet rivet --project Api.csproj` | Same — contracts and controllers are interchangeable |
| **OpenAPI spec** | `dotnet rivet --from-openapi spec.json` | C# contracts + DTOs (feed back into row 1) |

## Install

```bash
dotnet add package Rivet.Attributes
dotnet tool install --global dotnet-rivet
```

## See it work

```csharp
[RivetClient]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<TaskDto>>> List(CancellationToken ct) { ... }
}
```

```bash
dotnet rivet --project Api.csproj --output ../ui/generated/rivet
```

```typescript
import { tasks } from "~/generated/rivet/client";
const items = await tasks.list(); // TaskDto[], fully typed
```

## Documentation

Full documentation, guides, and API reference at **[maxanstey-meridian.github.io/rivet](https://maxanstey-meridian.github.io/rivet)**.

## License

MIT
