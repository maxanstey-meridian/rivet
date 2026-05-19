<p align="center">
  <h1 align="center">Rivet</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Rivet.Attributes"><img src="https://img.shields.io/nuget/v/Rivet.Attributes?label=Rivet.Attributes" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/dotnet-rivet"><img src="https://img.shields.io/nuget/v/dotnet-rivet?label=dotnet-rivet" alt="NuGet" /></a>
    <a href="https://github.com/maxanstey-meridian/rivet/blob/main/LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="License" /></a>
  </p>
</p>

**End-to-end type safety between .NET and TypeScript.** Use your C# types and ASP.NET
endpoints as the source of truth for generated TypeScript types, a typed client, and optional
Zod validators.

> Rivet maps what actually survives the wire boundary: no drift, no handwritten TS mirrors, no
> schema language you need to learn first.

[oRPC](https://orpc.unnoq.com) gives you this when your server is TypeScript. Rivet gives you the same DX when your server is .NET.

## Install

```bash
dotnet add package Rivet.Attributes
dotnet tool install --global dotnet-rivet
```

## Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
```

By default this emits:

- `types/` for generated TypeScript types
- `client/` for generated client modules
- `rivet.ts` for runtime configuration and fetch helpers

## C# Types -> TS Types

```csharp
public enum WorkItemStatus { Draft, Open, InProgress, Review, Done, Cancelled }

public sealed record Email(string Value);

public sealed record MemberDto(Guid Id, string Name, Email Email, string Role);

public sealed record CreateTaskResult(Guid Id, DateTime CreatedAt);
```

```ts
export type WorkItemStatus =
  | "draft"
  | "open"
  | "inProgress"
  | "review"
  | "done"
  | "cancelled";

export type Email = string & { readonly __brand: "Email" };

export type MemberDto = {
  id: string;
  name: string;
  email: Email;
  role: string;
};

export type CreateTaskResult = {
  id: string;
  createdAt: string;
};
```

## ASP.NET Endpoints -> Typed Client

Rivet works with ordinary ASP.NET controllers. Mark the endpoints you want to surface to
TypeScript and generate a client from the real transport shape.

```csharp
// Server-side DTOs and the annotated ASP.NET endpoint.
public enum WorkItemStatus { Draft, Open, InProgress, Review, Done, Cancelled }

public sealed record TaskDetailDto(
    string Title,
    WorkItemStatus Status,
    List<string> Labels,
    string? Description);

public sealed record NotFoundDto(string Message);

[ApiController]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [RivetEndpoint]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        return Ok(default(TaskDetailDto));
    }
}
```

Rivet generates the matching TypeScript DTOs and client overloads:

```ts
// Generated from the C# DTOs above.
export type TaskDetailDto = {
  title: string;
  status: WorkItemStatus;
  labels: string[];
  description?: string | null;
};

export type NotFoundDto = {
  message: string;
};

// Status-aware result shape for the non-throwing flow.
export type GetResult = RivetResultOf<
  | { status: 200; data: TaskDetailDto; response: Response }
  | { status: 404; data: NotFoundDto; response: Response }
  | { status: Exclude<number, 200 | 404>; data: unknown; response: Response }
>;

export function get(input: { params: { id: string; }; }): Promise<TaskDetailDto>;
export function get(
  input: { params: { id: string; }; },
  opts: { unwrap: false },
): Promise<GetResult>;
```

Use the generated client in either throwing or status-aware mode:

```ts
import { tasks } from "./generated/client/index.js";
import { configureRivet } from "./generated/rivet.js";

// Configure the generated client once at app startup.
configureRivet({ baseUrl: "https://api.example.com" });

// Fully type-safe, throws on error.
const task = await tasks.get({ params: { id: taskId } });

// Set `unwrap: false` when you want the full status-aware union instead of throwing.
const result = await tasks.get({ params: { id: taskId } }, { unwrap: false });
if (result.isNotFound()) {
  console.error(result.data.message);
}
```

## Runtime Validation

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --compile
```

With validation enabled, generated clients automatically validate incoming responses with [Zod](https://github.com/colinhacks/zod) at the network boundary.

- `schemas.ts`
- `validators.ts`

```ts
import { fromJSONSchema } from "zod";
import { TaskDetailDtoSchema } from "./schemas.js";
import type { TaskDetailDto } from "./types/controllers.js";

// Generated once from the emitted JSON Schema.
const _assertTaskDetailDto = fromJSONSchema(TaskDetailDtoSchema);

// Generated validation helper for use anywhere.
export const assertTaskDetailDto = (data: unknown): TaskDetailDto =>
  _assertTaskDetailDto.parse(data) as TaskDetailDto;
```

## Advanced Features

Rivet also supports:

- contract-driven APIs with [`[RivetContract]`](https://maxanstey-meridian.github.io/rivet/guides/contracts)
- minimal API hosts
- [OpenAPI emission](https://maxanstey-meridian.github.io/rivet/guides/openapi-emission) and [OpenAPI import](https://maxanstey-meridian.github.io/rivet/guides/openapi-import)
- the TypeScript-first sibling project [rivet-ts](https://github.com/maxanstey-meridian/rivet-ts)

## Documentation

Start with:

- [Getting Started](https://maxanstey-meridian.github.io/rivet/getting-started)
- [CLI Reference](https://maxanstey-meridian.github.io/rivet/reference/cli)
- [Runtime Validation](https://maxanstey-meridian.github.io/rivet/guides/runtime-validation)
- [Contracts](https://maxanstey-meridian.github.io/rivet/guides/contracts)
- [OpenAPI Emission](https://maxanstey-meridian.github.io/rivet/guides/openapi-emission)
- [OpenAPI Import](https://maxanstey-meridian.github.io/rivet/guides/openapi-import)

## License

MIT
