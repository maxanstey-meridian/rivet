<p align="center">
  <img src="logo.png" alt="Rivet" width="200" />
  <h1 align="center">Rivet</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Rivet.Attributes"><img src="https://img.shields.io/nuget/v/Rivet.Attributes?label=Rivet.Attributes" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/dotnet-rivet"><img src="https://img.shields.io/nuget/v/dotnet-rivet?label=dotnet-rivet" alt="NuGet" /></a>
    <a href="https://github.com/maxanstey-meridian/rivet/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="License" /></a>
  </p>
</p>

**End-to-end type safety between .NET and TypeScript.** No drift, no schema files, no codegen config.

[oRPC](https://orpc.unnoq.com) gives you this when your server is TypeScript. Rivet gives you the same DX when your server is .NET — richer types than an OpenAPI generator, because it reads Roslyn's full type graph instead of a JSON schema intermediary.

## Install

```bash
dotnet add package Rivet.Attributes
dotnet tool install --global dotnet-rivet
```

## Mark your C# types → get TypeScript types

```csharp
public enum Priority { Low, Medium, High, Critical }
public sealed record Email(string Value);               // single-property → branded
public sealed record TaskItem(Guid Id, string Title, Priority Priority, Email Author);
```

```typescript
// Generated
export type Priority = "Low" | "Medium" | "High" | "Critical";
export type Email = string & { readonly __brand: "Email" };
export type TaskItem = { id: string; title: string; priority: Priority; author: Email };
```

## Mark your controllers → get a typed client

```csharp
[RivetClient]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), 200)]
    [ProducesResponseType(typeof(NotFoundDto), 404)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) { ... }
}
```

```typescript
// Generated — discriminated union, narrowable by status
export type GetResult =
  | { status: 200; data: TaskDetailDto; response: Response }
  | { status: 404; data: NotFoundDto; response: Response };

const task = await tasks.get(id);                        // → TaskDetailDto (throws on error)
const result = await tasks.get(id, { unwrap: false });   // → GetResult (no throw)
```

## Or define contracts → get compile-time enforcement

```csharp
// Define the API surface — pure Rivet, no ASP.NET dependency
[RivetContract]
public static class MembersContract
{
    public static readonly RouteDefinition<List<MemberDto>> List =
        Define.Get<List<MemberDto>>("/api/members")
            .Description("List all team members");
}

// Implement it — compiler enforces the return type matches the contract
[HttpGet]
public async Task<IActionResult> List(CancellationToken ct)
    => (await MembersContract.List.Invoke(async () =>
    {
        return await db.Members.ToListAsync(ct); // must return List<MemberDto>
    })).ToActionResult();

// Works with minimal APIs too — --check verifies coverage for MapGet/MapPost/etc.
app.MapGet("/api/members", async (AppDb db, CancellationToken ct) =>
    (await MembersContract.List.Invoke(async () =>
    {
        return await db.Members.ToListAsync(ct);
    })).ToResult());  // you write ToResult() once, same pattern as ToActionResult()
```

## Add `--compile` → get runtime validation via typia

```bash
dotnet rivet --project Api.csproj --output ./generated --compile
```

Every API response is validated at the network boundary with [typia](https://typia.io) runtime assertions. If the server sends unexpected data, you get a clear error immediately — not a silent `undefined` three components later.

## Import OpenAPI → get C# contracts

Another team owns the API? Import their OpenAPI spec, get typed C# contracts, feed them back into the pipeline. The compiler tells you what broke when the upstream spec changes.

```json
{
  "components": {
    "schemas": {
      "TaskDto": {
        "type": "object",
        "properties": {
          "id": { "type": "string", "format": "uuid" },
          "title": { "type": "string" },
          "priority": { "$ref": "#/components/schemas/Priority" }
        },
        "required": ["id", "title", "priority"]
      },
      "Priority": {
        "type": "string",
        "enum": ["low", "medium", "high", "critical"]
      }
    }
  },
  "paths": {
    "/api/tasks": {
      "get": {
        "tags": ["Tasks"],
        "summary": "List all tasks",
        "responses": {
          "200": {
            "content": { "application/json": { "schema": {
              "type": "array", "items": { "$ref": "#/components/schemas/TaskDto" }
            } } }
          }
        }
      }
    }
  }
}
```

```bash
dotnet rivet --from-openapi spec.json --namespace MyApp.Contracts --output ./src/
```

```csharp
// Generated — sealed records, enums, typed contract with builder chain
public enum Priority { Low, Medium, High, Critical }

public sealed record TaskDto(Guid Id, string Title, Priority Priority);

[RivetContract]
public static class TasksContract
{
    public static readonly RouteDefinition<List<TaskDto>> List =
        Define.Get<List<TaskDto>>("/api/tasks")
            .Description("List all tasks");
}
```

## Documentation

Guides, reference, and architecture at **[maxanstey-meridian.github.io/rivet](https://maxanstey-meridian.github.io/rivet)**.

## License

MIT
