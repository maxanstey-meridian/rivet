# Rivet

End-to-end type safety from .NET to TypeScript. Roslyn reads your C# types and endpoints directly — no OpenAPI schema in the middle — and emits a typed TS client with optional runtime validation at the fetch boundary.

Two attributes, one typed client. Flat functions, not HTTP ceremony.

## What it solves

OpenAPI round-trip (C# → JSON schema → codegen → TS) loses nullable semantics, mangles enums, can't express discriminated unions, and forces controller ceremony for metadata. Roslyn has the full type graph — sealed records, non-nullable defaults, `required` properties — and can emit strictly richer TS types than any schema intermediary.

Inspired by [tRPC](https://trpc.io) and [oRPC](https://orpc.unnoq.com), which give you end-to-end type safety when your server is TypeScript. Rivet gives you the same DX when your server is .NET.

## What it produces

```
generated/rivet/
├── types.ts                  # export type Foo = { ... }
├── rivet.ts                  # configureRivet() + rivetFetch base
├── client/
│   ├── caseStatuses.ts       # export const list = () => rivetFetch(...)
│   └── submissions.ts        # one file per controller
├── validators.ts             # typia source (inert until compiled)
└── build/                    # (after --compile)
    ├── validators.js          # runtime assertion functions
    └── validators.d.ts
```

## Type mapping

| C# | TypeScript |
|---|---|
| `string`, `Guid` | `string` |
| `int`, `long`, `decimal`, `double` | `number` |
| `bool` | `boolean` |
| `DateTime`, `DateTimeOffset`, `DateOnly` | `string` |
| `T?` (nullable value/ref) | `T \| null` |
| `List<T>`, `T[]`, `IEnumerable<T>`, `IReadOnlyList<T>` | `T[]` |
| `Dictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `Record<string, T>` |
| `sealed record` | `type { ... }` (transitive discovery) |
| `enum` (with `JsonStringEnumConverter`) | `"A" \| "B" \| "C"` |
| `PagedResult<T>` (generic record) | `PagedResult<T>` |
| `JsonElement`, `JsonNode` | `unknown` |

## Dependencies

**Your project:**
- `Rivet.Attributes` NuGet package (two marker attributes, zero dependencies)

**The CLI tool:**
- .NET 10 SDK
- `Rivet.Tool` NuGet package (dotnet tool)
- Node.js on PATH (only if using `--compile` for typia validators)

## Quick start

### 1. Install

```bash
# Add the attributes to your API project
dotnet add package Rivet.Attributes

# Install the CLI tool
dotnet tool install --global Rivet.Tool
```

### 2. Mark your types

```csharp
using Rivet;

// Application-layer types get [RivetType]
[RivetType]
public sealed record CreateMessageCommand(Guid SubmissionId, string Body, MessageVisibility Visibility);

[RivetType]
public sealed record MessageDto(Guid Id, string Body, string AuthorName, DateTime CreatedAt);

// Domain types are discovered transitively — no attribute needed
public enum MessageVisibility { Internal, Public }
```

### 3. Mark your endpoints

```csharp
[RivetEndpoint]
[HttpPost("{id:guid}/messages")]
[ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
public async Task<IActionResult> CreateMessage(
    Guid id,
    [FromBody] CreateMessageCommand body,
    CancellationToken ct)
{
    // ...
}
```

`[RivetEndpoint]` is a marker only. HTTP method, route, and return type are read from ASP.NET's own attributes. Route params are matched by name from the template. `CancellationToken` and DI params are skipped automatically.

### 4. Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet
```

### 5. Use

```typescript
import { configureRivet } from "~/generated/rivet/rivet";
import * as messages from "~/generated/rivet/client/messages";

// Configure once
configureRivet({
  baseUrl: "http://localhost:5000",
  headers: () => ({ Authorization: `Bearer ${token}` }),
});

// Fully typed — params, body, and return type
const msg = await messages.createMessage(submissionId, {
  submissionId,
  body: "Hello",
  visibility: "Public",
});
```

### 6. Optional: runtime validation

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet --compile
```

This compiles [typia](https://typia.io) validators and re-emits the client with runtime type assertions at every fetch boundary. If the server sends unexpected data, you get a clear error instead of a silent `undefined` three components later.

## How it works

Rivet is a CLI tool (not a source generator) that uses Roslyn's `MSBuildWorkspace` to open your project, analyse the compilation, and emit `.ts` files. Same model as `dotnet-ef` or `dotnet-format`.

1. Opens your `.csproj` via `MSBuildWorkspace`
2. Finds `[RivetType]` records → walks the type graph, transitively discovers referenced types
3. Finds `[RivetEndpoint]` methods → reads `[HttpGet]`, `[Route]`, `[ProducesResponseType]`, `[FromBody]` etc.
4. Emits `types.ts`, per-controller client files, and optionally `validators.ts`
5. `--compile` runs `tsc` with the typia transformer to produce runtime validators

## Controller support

Rivet works with standard ASP.NET controllers returning `IActionResult`. Return types are extracted from `[ProducesResponseType(typeof(T), 200)]`. Controller `[Route]` prefixes are combined with method routes. Route constraints (`{id:guid}`) are stripped automatically.

Endpoints are grouped by controller into separate client files: `CaseStatusesController` → `client/caseStatuses.ts`.

## Limitations

- Records only — no inheritance, no polymorphism
- `delete` is renamed to `remove` in generated clients (TS reserved word)
- Single success type per endpoint (first 2xx `ProducesResponseType`)
- No `IFormFile` / multipart — manual escape hatch
- No SignalR / WebSocket support

## License

MIT
