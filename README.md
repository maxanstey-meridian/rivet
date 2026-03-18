# Rivet

Your C# types are your TypeScript types. No drift, no schema, no codegen config.

Rivet reads your .NET sealed records and controller endpoints via Roslyn and emits typed TypeScript — shared types and
a typed HTTP client — with optional runtime validation at the fetch boundary.

Three attributes. One command. Full-stack type safety.

## Why

[tRPC](https://trpc.io) and [oRPC](https://orpc.unnoq.com) give you end-to-end type safety when your server is
TypeScript. Rivet gives you the same DX when your server is .NET.

Unlike OpenAPI-based generators (NSwag, Kiota, Kubb), Rivet reads Roslyn's full type graph — nullable annotations,
sealed records, string enum unions, generic type parameters — and produces richer TypeScript types than any JSON schema
intermediary can represent.

Rivet is not just a client generator. Any C# type marked with `[RivetType]` becomes a TypeScript type — whether or not
it appears in an endpoint. Commands, results, value objects, DTOs — if your frontend and backend need to agree on a
shape, mark it once in C# and it appears in your generated types. The types are the primary output; the client is a
bonus.

## What it produces

```
generated/rivet/
├── types/
│   ├── index.ts              # barrel: export * as common, export * as domain, ...
│   ├── common.ts             # types referenced across multiple groups
│   ├── domain.ts             # types grouped by C# namespace
│   └── contracts.ts
├── rivet.ts                  # configureRivet(), rivetFetch, RivetError
├── client/
│   ├── index.ts              # barrel: export * as tasks, export * as members
│   ├── tasks.ts              # export const list = () => rivetFetch(...)
│   └── members.ts            # one file per controller
├── validators.ts             # typia source (inert until compiled)
└── build/                    # (after --compile)
    ├── validators.js          # runtime assertion functions
    └── validators.d.ts
```

Types are split by C# namespace. Types referenced across multiple groups are promoted to `common.ts`. Barrel exports
let consumers import from `types/index.js` — the grouping is purely for navigating the generated code.

## Type mapping

| C#                                                        | TypeScript                               |
|-----------------------------------------------------------|------------------------------------------|
| `string`, `Guid`                                          | `string`                                 |
| `int`, `long`, `decimal`, `double`, `uint`, `ulong`       | `number`                                 |
| `bool`                                                    | `boolean`                                |
| `DateTime`, `DateTimeOffset`, `DateOnly`                  | `string`                                 |
| `T?` (nullable value/ref)                                 | `T \| null`                              |
| `List<T>`, `T[]`, `IEnumerable<T>`, `IReadOnlyList<T>`    | `T[]`                                    |
| `Dictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `Record<string, T>`                      |
| `sealed record`                                           | `type { ... }` (transitive discovery)    |
| `enum` (with `JsonStringEnumConverter`)                   | `export type Status = "A" \| "B"`        |
| `PagedResult<T>` (generic record)                         | `PagedResult<T>`                         |
| `JsonElement`, `JsonNode`                                 | `unknown`                                |
| `JsonObject`                                              | `Record<string, unknown>`                |
| `JsonArray`                                               | `unknown[]`                              |
| `Email(string Value)` (single-property VO)                | `string & { readonly __brand: "Email" }` |

## Dependencies

**Your project:**

- `Rivet.Attributes` NuGet package — three marker attributes, zero dependencies

**The CLI tool:**

- .NET 8+ SDK
- `dotnet-rivet` global tool
- Node.js on PATH (only required for `--compile`)

## Quick start

### 1. Install

```bash
# Add the attributes to your API project
dotnet add package Rivet.Attributes

# Install the CLI tool
dotnet tool install --global dotnet-rivet
```

### 2. Mark your types and endpoints

```csharp
namespace MyApp.Domain;

// No attributes needed — discovered transitively via endpoint types
public enum Priority { Low, Medium, High, Critical }
public sealed record Email(string Value);  // VO → branded type in TS

// Shared with the frontend but not exposed via any endpoint
[RivetType]
public sealed record TaskItem(Guid Id, string Title, Priority Priority, Email Author, DateTime CreatedAt);

namespace MyApp.Contracts;

// No [RivetType] needed — discovered via the endpoint
public sealed record CreateTaskCommand(string Title, Priority Priority, Email Author);
public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);

namespace MyApp.Api;

// [RivetClient] auto-discovers all public HTTP methods on this controller
[RivetClient]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateTaskResult), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTaskCommand command,
        CancellationToken ct)
    {
        // ...
    }
}
```

Generates:

```typescript
// types/common.ts — Email, Priority (referenced by both domain.ts and contracts.ts)
export type Email = string & { readonly __brand: "Email" };
export type Priority = "Low" | "Medium" | "High" | "Critical";

// types/domain.ts — from MyApp.Domain
import type {Email, Priority} from "./common.js";

export type TaskItem = {
  id: string;
  title: string;
  priority: Priority;
  author: Email;
  createdAt: string;
};

// types/contracts.ts — from MyApp.Contracts
import type {Email, Priority} from "./common.js";

export type CreateTaskCommand = {
  title: string;
  priority: Priority;
  author: Email;
};

export type CreateTaskResult = {
  id: string;
  createdAt: string;
};

// client/tasks.ts
export const create = (command: CreateTaskCommand): Promise<CreateTaskResult> =>
  rivetFetch<CreateTaskResult>("POST", `/api/tasks`, {body: command});
```

Request types (`[FromBody]`), response types (`[ProducesResponseType]` or typed returns), and everything they reference
(enums, VOs, nested records) are discovered transitively — `[RivetType]` is only needed for types not reachable from
any endpoint.

### 3. Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet
```

### 4. Use

```typescript
import {configureRivet} from "~/generated/rivet/rivet";
import {tasks} from "~/generated/rivet/client";

// Configure once at app startup
configureRivet({
  baseUrl: "http://localhost:5000",
  headers: () => ({Authorization: `Bearer ${token}`}),
});

// Returns T directly — throws RivetError on non-2xx
const result = await tasks.create({
  title: "Fix the thing",
  priority: "High",
  author: "max@example.com" as Email,
});
console.log(result.id);        // string
console.log(result.createdAt); // string
```

### 5. Optional: runtime validation

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --compile
```

Compiles [typia](https://typia.io) validators and re-emits the client with runtime type assertions at every fetch
boundary. If the server sends unexpected data, you get a clear error instead of a silent `undefined` three components
later.

## Endpoint discovery

Two attributes control which methods become client endpoints:

**`[RivetClient]`** — class-level. All public methods with an HTTP attribute (`[HttpGet]`, `[HttpPost]`, etc.) become
endpoints automatically. No per-method annotation needed. Best for controllers where every action is an API endpoint.

**`[RivetEndpoint]`** — method-level. Explicit opt-in per method. Works on any class, no base class required. Use when
only some methods on a class should be exposed, or for standalone endpoints outside the controller pattern:

```csharp
[RivetClient]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    // Included — public method with HTTP attribute on a [RivetClient] class
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaskDetailDto>> Get(Guid id, CancellationToken ct) { ... }

    // Excluded — no [RivetEndpoint], and this is a helper not an action
    public IActionResult RedirectToTask(Guid id) => RedirectToAction(nameof(Get), new { id });
}

// Standalone endpoint — no controller, no base class
public static class HealthCheck
{
    [RivetEndpoint]
    [HttpGet("/api/health")]
    public static Task<HealthDto> Get() => Task.FromResult(new HealthDto("ok"));
}
```

Both attributes can coexist — if a method matches both `[RivetClient]` (via its class) and `[RivetEndpoint]`, it is
emitted once.

### Return type inference

Return types are inferred from (in order of precedence):

1. `[ProducesResponseType(typeof(T), 200)]` — preferred, works with `IActionResult`
2. `ActionResult<T>` — unwrapped automatically
3. `Task<T>` — for static method endpoints

### Route handling

Controller `[Route]` prefixes are combined with method routes. Route constraints (`{id:guid}`) are stripped
automatically. Route params without `[FromRoute]` are matched by name from the template. `CancellationToken` and DI
params are excluded automatically.

Endpoints are grouped by controller into separate client files: `TasksController` becomes `client/tasks.ts`.

## Error handling

Every endpoint function returns `Promise<T>` directly. Non-2xx responses throw a `RivetError` with `status`,
`response`, `body` (parsed JSON when `content-type` is `application/json`, raw string otherwise), and `cause`.

```typescript
import {RivetError} from "~/generated/rivet/rivet";

try {
  await tasks.create({title: ""});
} catch (err) {
  if (err instanceof RivetError) {
    err.status;        // 422
    err.body?.message; // "Title is required" (parsed from JSON response)
    err.body?.code;    // "VALIDATION_ERROR"
  }
}
```

### Custom error handling

Use `onError` to intercept errors before they're thrown — useful for remapping to your own error class or triggering
side effects like session expiry:

```typescript
configureRivet({
  baseUrl: "...",
  onError: (err) => {
    if (err.status === 401) onSessionExpired();
    throw new MyApiError(err.status, err.body);
  },
});
```

### Custom fetch

The `fetch` config option accepts any `typeof fetch` — use it for credentials, auth retry, or request deduplication:

```typescript
const authFetch: typeof fetch = async (input, init) => {
  const res = await fetch(input, {...init, credentials: "include"});
  if (res.status === 401) {
    await refreshToken();
    return fetch(input, {...init, credentials: "include"});
  }
  return res;
};

configureRivet({baseUrl: "...", fetch: authFetch});
```

## Value objects

Single-property records with a property named `Value` are emitted as branded types:

```csharp
// C# — no Rivet attribute needed
public sealed record Email(string Value);
public sealed record Uprn(string Value);
public sealed record Quantity(int Value);
```

```typescript
// TypeScript — branded primitives, nominal type safety
export type Email = string & { readonly __brand: "Email" };
export type Uprn = string & { readonly __brand: "Uprn" };
export type Quantity = number & { readonly __brand: "Quantity" };
```

Multi-property records are emitted as regular object types: `Money(decimal Amount, string Currency)` becomes
`{ amount: number; currency: string }`.

## File uploads

`IFormFile` parameters are detected automatically and emitted as `File` in TypeScript. The generated client constructs
`FormData` and lets the browser set the correct `Content-Type` with multipart boundary:

```csharp
[RivetEndpoint]
[HttpPost("{id:guid}/attachments")]
[ProducesResponseType(typeof(AttachmentResultDto), StatusCodes.Status201Created)]
public async Task<IActionResult> Attach(Guid id, IFormFile file, CancellationToken ct)
{
    // ...
}
```

```typescript
// Generated
export const attach = (id: string, file: File): Promise<AttachmentResultDto> => {
  const fd = new FormData();
  fd.append("file", file);
  return rivetFetch<AttachmentResultDto>("POST", `/api/tasks/${id}/attachments`, {body: fd});
};
```

## How it works

Rivet is a CLI tool (not a source generator) that uses Roslyn's `MSBuildWorkspace` to open your project, analyse the
compilation, and emit `.ts` files — similar to `dotnet-ef` or `dotnet-format`.

1. Opens your `.csproj` via `MSBuildWorkspace`
2. Finds `[RivetType]` records and transitively discovers all referenced types
3. Finds `[RivetClient]` classes and `[RivetEndpoint]` methods — reads `[HttpGet]`, `[Route]`,
   `[ProducesResponseType]`, `[FromBody]`, etc.
4. Groups types by C# namespace, promotes cross-referenced types to `common.ts`
5. Emits per-controller client files and optionally `validators.ts`
6. With `--compile`, runs `tsc` with the typia transformer to produce runtime validators

## Sample project

The repo includes a sample ASP.NET project at `samples/TaskBoard.Api/` — a task board API with two controllers, domain
enums, value objects, application-layer commands with colocated results, and a generic `PagedResult<T>`.

```
samples/TaskBoard.Api/
├── Domain/
│   ├── Priority.cs              # Priority, WorkItemStatus enums
│   ├── Label.cs                 # Label record (multi-property → object type)
│   └── ValueObjects.cs          # Email, TaskId VOs (single Value → branded types)
├── Application/
│   ├── Ports/ITaskRepository.cs
│   ├── CreateTask/              # Command + Result colocated with use case
│   └── PagedResult.cs           # Generic wrapper
├── Controllers/
│   ├── TasksController.cs       # 7 endpoints, colocated DTOs
│   └── MembersController.cs     # 3 endpoints, colocated DTOs
└── Program.cs
```

Run Rivet against it:

```bash
# Preview to stdout
dotnet run --project Rivet.Tool -- --project samples/TaskBoard.Api/TaskBoard.Api.csproj

# Write to disk
dotnet run --project Rivet.Tool -- --project samples/TaskBoard.Api/TaskBoard.Api.csproj --output /tmp/rivet-output

# Write to disk + compile typia validators (requires Node.js)
dotnet run --project Rivet.Tool -- --project samples/TaskBoard.Api/TaskBoard.Api.csproj --output /tmp/rivet-output --compile
```

## Limitations

- Records only — no inheritance, no polymorphism
- `delete` is renamed to `remove` in generated clients (reserved word in TypeScript)
- Single file uploads only — no `IFormFileCollection` or `List<IFormFile>`
- No SignalR or WebSocket support

## License

MIT
