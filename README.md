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

[oRPC](https://orpc.unnoq.com) gives you this when your server is TypeScript. Rivet gives you the same DX when your server is .NET.

> **New here?** Follow the [**Tutorial: Zero to Typed Client**](https://maxanstey-meridian.github.io/rivet/guides/tutorial) — `dotnet new webapi` to a fully typed TS client in under 5 minutes.

## Install

```bash
dotnet add package Rivet.Attributes --version "*"
dotnet tool install --global dotnet-rivet
```

## Mark your C# types → get TypeScript types

```csharp
[RivetType]
public enum Priority { Low, Medium, High, Critical }

[RivetType]
public sealed record Email(string Value); // single-property → branded

[RivetType]
public sealed record TaskItem(Guid Id, string Title, Priority Priority, Email Author);

[RivetType]
public sealed record ErrorDto(string Code, string Message);
```

```typescript
// Generated
export type Priority = "Low" | "Medium" | "High" | "Critical";
export type Email = string & { readonly __brand: "Email" };
export type TaskItem = { id: string; title: string; priority: Priority; author: Email };
export type ErrorDto = { code: string; message: string };
```

## Mark your controllers → get a typed client

```csharp
[RivetClient]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ErrorDto), 404)]
    public async Task<ActionResult<TaskDetailDto>> Get(Guid id, CancellationToken ct) { ... }
}
```

```typescript
// Generated — discriminated union, narrowable by status
export type GetResult =
  | { status: 200; data: TaskDetailDto; response: Response }
  | { status: 404; data: ErrorDto; response: Response }
  | { status: Exclude<number, 200 | 404>; data: unknown; response: Response };

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

// Works with minimal APIs too — .Route avoids duplicating the route string
app.MapGet(MembersContract.List.Route, async (AppDb db, CancellationToken ct) =>
    (await MembersContract.List.Invoke(async () =>
    {
        return await db.Members.ToListAsync(ct);
    })).ToResult());  // you write ToResult() once, same pattern as ToActionResult()
```

## Add `--compile` → get runtime validation via typia

```bash
dotnet rivet --project Api.csproj --output ./generated --compile
```

Rivet generates [typia](https://typia.io) validators for your types:

```csharp
[RivetType]
public sealed record Age(int Value);
```

```typescript
// Generated type — number at runtime, branded at compile time
export type Age = number & { readonly __brand: "Age" };

// Generated validator — typia compiles it to:
export const assertAge = typia.createAssert<Age>();
```

```javascript
// What typia emits at runtime — a pure function, no reflection, no runtime schema
export const assertAge = (input) => {
  if (typeof input !== "number") {
    throw new TypeGuardError({ expected: "Age", value: input });
  }
  
  return input;
};
```

Every API response is validated at the network boundary with [typia](https://typia.io) runtime assertions — not just primitives, but full object shapes, nested types, and unions. If the server sends unexpected data, you get a clear error immediately — not a silent `undefined` three components later.

## Or `--compile zod` → validate with Zod 4, no compile step

```bash
dotnet rivet --project Api.csproj --output ./generated --compile zod
```

Same validation wiring as typia, but backed by Zod 4's `fromJSONSchema()` — no typia transformer, no node compile step. Rivet emits a `schemas.ts` with standalone JSON Schema definitions and a `validators.ts` that wraps them:

```typescript
// schemas.ts — standalone JSON Schema, usable with any validator
export const TaskItemSchema = { "$ref": "#/$defs/TaskItem", "$defs": $defs } as const;

// validators.ts — cached Zod schemas, same assertFoo interface as typia
import { fromJSONSchema, z } from "zod";
const _assertTaskItem = fromJSONSchema(TaskItemSchema as any);
export const assertTaskItem = (data: unknown): TaskItem => _assertTaskItem.parse(data) as TaskItem;
```

The generated client imports from `validators.ts` identically to the typia path — consumers don't need to know which backend is wired in.

You can also emit just the schemas without validation wiring:

```bash
dotnet rivet --project Api.csproj --output ./generated --jsonschema
```

This writes `schemas.ts` only — use it with `fromJSONSchema()`, ajv, or any JSON Schema consumer.

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

## Check contract coverage

```bash
dotnet rivet --project Api.csproj --check
```

```
warning: [MissingImplementation] MembersContract.Remove: expected DELETE /api/members/{id}, got (none)
warning: [RouteMismatch] MembersContract.UpdateRole: expected /api/members/{id}/role, got /api/members/{id}/update-role
Coverage: 2/4 endpoints covered, 1 mismatch(es), 1 missing.
```

Verifies that every contract endpoint has a matching handler implementation, with correct HTTP method and route. Useful in CI to catch missing or mismatched handlers.

## List your routes

```bash
dotnet rivet --project Api.csproj --routes
```

```
  Method  Route                      Handler
  ──────  ─────────────────────────  ───────
  GET     /api/members               members.list
  POST    /api/members               members.invite
  DELETE  /api/members/{id}          members.remove
  PUT     /api/members/{id}/role     members.updateRole
4 route(s).
```

## Documentation

Guides, reference, and architecture at **[maxanstey-meridian.github.io/rivet](https://maxanstey-meridian.github.io/rivet)**.

## License

MIT
