# Tutorial

This walks the whole pipeline on a single file — no project setup needed — then
shows the project-mode equivalent. Every command below has been run as written.

## 1. Install

```bash
dotnet tool install --global dotnet-rivet
```

(For a real project you also `dotnet add package Rivet.Attributes`; in single-file
mode the tool compiles your file against its own copy of the attributes.)

## 2. Write a contract

`Contracts.cs`:

```csharp
using System;
using Rivet;

namespace Demo;

public enum TaskStatus { Open, Done }

[RivetType]
public sealed record TaskDto(Guid Id, string Title, TaskStatus Status);

[RivetContract]
public static class TasksContract
{
    public static readonly RouteDefinition<TaskDto> Get =
        Define.Get<TaskDto>("/api/tasks/{id}")
            .Description("Fetch a task");
}
```

## 3. Emit the spec

```bash
dotnet rivet Contracts.cs --output ./generated
```

Output:

```
  openapi.json → ./generated/openapi.json
Generated OpenAPI spec: 1 schemas, 1 endpoints.
```

The spec is OpenAPI 3.1. The interesting parts:

```jsonc
// components/schemas
"TaskStatus": { "type": "string", "enum": ["open", "done"] },
"TaskDto": {
  "type": "object",
  "properties": {
    "id":     { "type": "string", "format": "uuid" },
    "title":  { "type": "string" },
    "status": { "$ref": "#/components/schemas/TaskStatus" }
  },
  "required": ["id", "title", "status"]
}
```

Note the defaults: property names and enum values are camelCased, `Guid` becomes
`format: uuid`, and non-nullable members are `required`.

## 4. Project mode

For a real API, point Rivet at the `.csproj` instead — same output, full semantic
model (controllers, minimal APIs, and contracts are all discovered):

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated
```

Omit `--output` to preview the spec on stdout. Add `--check` to verify contract
coverage, `--routes` to list discovered endpoints.

## 5. Consume the spec

```bash
npx openapi-typescript ./generated/openapi.json -o ./src/api/schema.d.ts
npm install openapi-fetch
```

```ts
import createClient from "openapi-fetch";
import type { paths } from "./api/schema";

const api = createClient<paths>({ baseUrl: "https://api.example.com" });
const { data, error } = await api.GET("/api/tasks/{id}", {
  params: { path: { id: taskId } },
});
```

## Next

- [Contracts guide](/guides/contracts) — `.Invoke()`, error responses, security
- [Runtime Validation](/guides/runtime-validation) — what is and isn't enforced at runtime
- [CLI Reference](/reference/cli)
