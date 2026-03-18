# Rivet

Your C# types are your TypeScript types. No drift, no schema, no codegen config.

Rivet reads your .NET sealed records and controller endpoints directly via Roslyn and emits typed TypeScript — both
shared types and a typed HTTP client — with optional runtime validation at the fetch boundary.

Two attributes. One command. Full-stack type safety.

## Why

[tRPC](https://trpc.io) and [oRPC](https://orpc.unnoq.com) give you end-to-end type safety when your server is
TypeScript. Rivet gives you the same DX when your server is .NET.

Unlike OpenAPI-based generators (NSwag, Kiota, Kubb), Rivet reads Roslyn's full type graph — nullable annotations,
sealed records, string enum unions, generic type parameters — and emits strictly richer TS types than any JSON schema
intermediary can represent.

## Shared types, not just HTTP

Rivet isn't just a client generator. Any C# type you mark with `[RivetType]` becomes a TypeScript type — whether or not
it's used in an endpoint. Commands, results, value objects, DTOs — if your frontend and backend need to agree on a shape,
mark it once in C# and it appears in `types.ts`. No duplication, no manual sync, no drift.

This is currently impossible in the .NET → TypeScript ecosystem. Existing tools are HTTP-client-first: they generate
types as a byproduct of endpoint schemas. Rivet inverts this — the types are the primary output, the client is a bonus.

## What it produces

```
generated/rivet/
├── types.ts                  # export type Foo = { ... }
├── rivet.ts                  # configureRivet(), rivetFetch, RivetError
├── client/
│   ├── tasks.ts              # export const list = () => rivetFetch(...)
│   └── members.ts            # one file per controller
├── validators.ts             # typia source (inert until compiled)
└── build/                    # (after --compile)
    ├── validators.js          # runtime assertion functions
    └── validators.d.ts
```

## Type mapping

| C#                                                        | TypeScript                            |
|-----------------------------------------------------------|---------------------------------------|
| `string`, `Guid`                                          | `string`                              |
| `int`, `long`, `decimal`, `double`                        | `number`                              |
| `bool`                                                    | `boolean`                             |
| `DateTime`, `DateTimeOffset`, `DateOnly`                  | `string`                              |
| `T?` (nullable value/ref)                                 | `T \| null`                           |
| `List<T>`, `T[]`, `IEnumerable<T>`, `IReadOnlyList<T>`    | `T[]`                                 |
| `Dictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `Record<string, T>`                   |
| `sealed record`                                           | `type { ... }` (transitive discovery) |
| `enum` (with `JsonStringEnumConverter`)                   | `export type Status = "A" \| "B"`     |
| `PagedResult<T>` (generic record)                         | `PagedResult<T>`                      |
| `JsonElement`, `JsonNode`                                 | `unknown`                             |
| `JsonObject`                                              | `Record<string, unknown>`             |
| `JsonArray`                                               | `unknown[]`                           |
| `Email(string Value)` (single-property VO)                | `string & { readonly __brand: "Email" }` |

## Dependencies

**Your project:**

- `Rivet.Attributes` NuGet package (two marker attributes, zero dependencies)

**The CLI tool:**

- .NET 8+ SDK
- `dotnet-rivet` NuGet package (dotnet tool)
- Node.js on PATH (only if using `--compile` for typia validators)

## Quick start

### 1. Install

```bash
# Add the attributes to your API project
dotnet add package Rivet.Attributes

# Install the CLI tool
dotnet tool install --global dotnet-rivet
```

### 2. Mark your endpoints

`[RivetEndpoint]` is all you need. Request types (`[FromBody]`), response types (`[ProducesResponseType]`), and
everything they reference (enums, VOs, nested records) are all discovered transitively — no `[RivetType]` required.

`[RivetType]` is only needed for types that aren't reachable from any endpoint (e.g. a shared DTO you want to emit
without an endpoint).

### 3. Example

```csharp
// Domain — no attributes needed, discovered transitively
public enum Priority { Low, Medium, High, Critical }
public sealed record Email(string Value);  // VO → branded type in TS

// DTOs — no [RivetType] needed, discovered via the endpoint
public sealed record CreateTaskCommand(string Title, Priority Priority, Email Author);
public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);

// Endpoint — this is the only attribute you need
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [RivetEndpoint]
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

HTTP method, route, and return type are read from ASP.NET's own attributes. Route params are matched by name from the
template. `CancellationToken` and DI params are skipped automatically.

### 4. Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet
```

### 5. Use

```typescript
import { configureRivet } from "~/generated/rivet/rivet";
import * as tasks from "~/generated/rivet/client/tasks";

// Configure once
configureRivet({
  baseUrl: "http://localhost:5000",
  headers: () => ({ Authorization: `Bearer ${token}` }),
});

// Returns T directly — throws RivetError on non-2xx
const result = await tasks.create({
  title: "Fix the thing",
  priority: "High",
  labelNames: ["bug"],
});
console.log(result.id); // CreateTaskResult

const task = await tasks.get(id);
console.log(task.title); // string
```

### 6. Optional: runtime validation

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --compile
```

This compiles [typia](https://typia.io) validators and re-emits the client with runtime type assertions at every fetch
boundary. If the server sends unexpected data, you get a clear error instead of a silent `undefined` three components
later.

## Try it

The repo includes a sample ASP.NET project at `samples/TaskBoard.Api/` — a realistic task board API with two
controllers, domain enums and value objects (discovered transitively), application-layer commands with colocated results,
and a generic `PagedResult<T>`.

```
samples/TaskBoard.Api/
├── Domain/
│   ├── Priority.cs              # Priority, WorkItemStatus enums
│   ├── Label.cs                 # Label record (multi-property, emits as object)
│   └── ValueObjects.cs          # Email, TaskId VOs (single Value property → branded types)
├── Application/
│   ├── Ports/ITaskRepository.cs
│   ├── CreateTask/              # Command + Result colocated with use case
│   └── PagedResult.cs           # Generic wrapper
├── Controllers/
│   ├── TasksController.cs       # 6 endpoints, colocated DTOs
│   └── MembersController.cs     # 3 endpoints, colocated DTOs
└── Program.cs
```

Run Rivet against it:

```bash
# Preview to stdout
dotnet run --project Rivet.Tool -- --project samples/TaskBoard.Api/TaskBoard.Api.csproj

# Write to disk
dotnet run --project Rivet.Tool -- --project samples/TaskBoard.Api/TaskBoard.Api.csproj --output /tmp/rivet-output

# Write to disk + compile typia validators (requires node)
dotnet run --project Rivet.Tool -- --project samples/TaskBoard.Api/TaskBoard.Api.csproj --output /tmp/rivet-output --compile
```

## How it works

Rivet is a CLI tool (not a source generator) that uses Roslyn's `MSBuildWorkspace` to open your project, analyse the
compilation, and emit `.ts` files. Same model as `dotnet-ef` or `dotnet-format`.

1. Opens your `.csproj` via `MSBuildWorkspace`
2. Finds `[RivetType]` records → walks the type graph, transitively discovers referenced types
3. Finds `[RivetEndpoint]` methods → reads `[HttpGet]`, `[Route]`, `[ProducesResponseType]`, `[FromBody]` etc.
4. Emits `types.ts`, per-controller client files, and optionally `validators.ts`
5. `--compile` runs `tsc` with the typia transformer to produce runtime validators

## Controller support

Rivet works with standard ASP.NET controllers. Return types are inferred from:

- `[ProducesResponseType(typeof(T), 200)]` — preferred, works with `IActionResult`
- `ActionResult<T>` — unwrapped automatically
- `Task<T>` — for minimal API / static method endpoints

Controller `[Route]` prefixes are combined with method routes. Route constraints (`{id:guid}`) are stripped
automatically. Route params without `[FromRoute]` are matched by name from the template.

Endpoints are grouped by controller into separate client files: `TasksController` → `client/tasks.ts`.

## Error handling

Every endpoint function returns `Promise<T>` directly. Non-2xx responses throw a `RivetError` with `status`,
`response`, `body` (parsed JSON when `content-type` is `application/json`, raw string otherwise), and `cause`.

```typescript
import { RivetError } from "~/generated/rivet/rivet";

try {
  await tasks.create({ title: "" });
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

The `fetch` config option accepts any `typeof fetch` — use it for credentials, auth retry, request deduplication:

```typescript
const authFetch: typeof fetch = async (input, init) => {
  const res = await fetch(input, { ...init, credentials: "include" });
  if (res.status === 401) {
    await refreshToken();
    return fetch(input, { ...init, credentials: "include" });
  }
  return res;
};

configureRivet({ baseUrl: "...", fetch: authFetch });
```

## Value objects

Records with a single property named `Value` are detected as value objects and emitted as branded types:

```csharp
// C# — domain layer, no Rivet attribute needed
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

Multi-property records are emitted as regular object types: `Money(decimal Amount, string Currency)` → `{ amount: number; currency: string }`.

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
export const attach = async (id: string, file: File): Promise<AttachmentResultDto> => {
  const fd = new FormData();
  fd.append("file", file);
  return rivetFetch<AttachmentResultDto>("POST", `/api/tasks/${id}/attachments`, { body: fd });
};
```

## Limitations

- Records only — no inheritance, no polymorphism
- `delete` is renamed to `remove` in generated clients (TS reserved word)
- No `IFormFileCollection` / `List<IFormFile>` — single file only for now
- No SignalR / WebSocket support

## License

MIT
