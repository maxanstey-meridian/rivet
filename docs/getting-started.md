# Getting Started

The happy path: annotate your C# types, generate TypeScript, use the typed client.

## 1. Install

```bash
# Add the attributes to your API project
dotnet add package Rivet.Attributes

# Install the CLI tool
dotnet tool install --global dotnet-rivet
```

**Dependencies:**
- .NET 8+ SDK
- `Rivet.Attributes` — marker attributes and contract builders, zero dependencies
- Node.js on PATH (only required for `--compile`)

## 2. Mark your types and endpoints

```csharp
[RivetType]  // explicit — for types not reachable from any endpoint
public sealed record TaskItem(Guid Id, string Title, Priority Priority, Email Author);

[RivetClient] // auto-discovers all public HTTP methods on this controller
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

Request types (`[FromBody]`), response types (`[ProducesResponseType]` or typed returns), and everything they reference (enums, VOs, nested records) are discovered transitively — `[RivetType]` is only needed for types not reachable from any endpoint.

## 3. Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ../ui/generated/rivet
```

## 4. What it produces

```
generated/rivet/
├── types/
│   ├── index.ts              # barrel: export * as common, export * as domain, ...
│   ├── common.ts             # types referenced across multiple groups
│   ├── domain.ts             # types grouped by C# namespace
│   └── contracts.ts
├── rivet.ts                  # configureRivet(), rivetFetch, RivetError, RivetResult
├── client/
│   ├── index.ts              # barrel: export * as tasks, export * as members
│   ├── tasks.ts              # overloaded functions with typed error responses
│   └── members.ts            # one file per controller
├── validators.ts             # typia source (inert until compiled)
└── build/                    # (after --compile)
    ├── validators.js          # runtime assertion functions
    └── validators.d.ts
```

Types are split by C# namespace. Types referenced across multiple groups are promoted to `common.ts`. Barrel exports let consumers import from `types/index.js` — the grouping is purely for navigating the generated code.

## 5. Use

```typescript
import { configureRivet } from "~/generated/rivet/rivet";
import { tasks } from "~/generated/rivet/client";

// Configure once at app startup
configureRivet({
  baseUrl: "http://localhost:5000",
  headers: () => ({ Authorization: `Bearer ${token}` }),
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

## Next steps

- Add [runtime validation](/guides/runtime-validation) with `--compile` for type assertions at the network boundary
- Define [contracts](/guides/contracts) for a decoupled API surface with compile-time enforcement
- Generate an [OpenAPI spec](/guides/openapi-emission) alongside your TypeScript output
- [Import an OpenAPI spec](/guides/openapi-import) to generate C# contracts from an external API
