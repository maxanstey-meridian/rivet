<p align="center">
  <h1 align="center">Rivet</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Rivet.Attributes"><img src="https://img.shields.io/nuget/v/Rivet.Attributes?label=Rivet.Attributes" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/dotnet-rivet"><img src="https://img.shields.io/nuget/v/dotnet-rivet?label=dotnet-rivet" alt="NuGet" /></a>
    <img src="https://img.shields.io/badge/license-MIT-blue" alt="License" />
  </p>
</p>

**Your C# is the contract.** Rivet reads your C# types and ASP.NET endpoints with Roslyn and
emits an OpenAPI 3.1 specification — no attributes-on-everything, no runtime
reflection, no drift between your declared C# types and what the spec says.

> Rivet maps what actually survives the wire boundary. The OpenAPI ecosystem does the rest:
> TypeScript types via [openapi-typescript](https://github.com/openapi-ts/openapi-typescript),
> a typed client via [openapi-fetch](https://github.com/openapi-ts/openapi-typescript/tree/main/packages/openapi-fetch),
> Zod schemas via [openapi-zod-client](https://github.com/astahmer/openapi-zod-client),
> docs via any OpenAPI renderer.

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

This writes `./generated/openapi.json` — an OpenAPI 3.1 spec derived from your compiled
C# (Roslyn semantic model, not runtime reflection). Omit `--output` to preview the spec
on stdout.

## C# Types → OpenAPI Schemas

```csharp
public enum WorkItemStatus { Draft, Open, InProgress, Review, Done, Cancelled }

public sealed record Email(string Value);

public sealed record MemberDto(Guid Id, string Name, Email Email, string Role);
```

```jsonc
// components/schemas (excerpt)
{
  "WorkItemStatus": {
    "type": "string",
    "enum": ["draft", "open", "inProgress", "review", "done", "cancelled"]
  },
  "Email": { "type": "string", "x-rivet-brand": "Email" },
  "MemberDto": {
    "type": "object",
    "properties": {
      "id": { "type": "string", "format": "uuid" },
      "name": { "type": "string" },
      "email": { "$ref": "#/components/schemas/Email" },
      "role": { "type": "string" }
    },
    "required": ["id", "name", "email", "role"]
  }
}
```

Value-object brands, generics (monomorphised), nullability, validation attributes
(`[Range]`, `[StringLength]`, `[RegularExpression]`, ...), descriptions, and examples all
flow into the spec — plus `x-rivet-*` vendor extensions that preserve C#-level fidelity
(brands, generics, contract names) through the spec
([vendor extensions reference](https://maxanstey-meridian.github.io/rivet/reference/vendor-extensions)).

## ASP.NET Endpoints → OpenAPI Operations

Rivet works with ordinary ASP.NET controllers. Mark the endpoints you want surfaced and
the operation is derived from the real transport shape:

```csharp
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

becomes `GET /api/tasks/{id}` with a typed `200` (TaskDetailDto) and `404` (NotFoundDto)
response — route constraints normalised, params classified (route/query/body/form/file),
multipart and form-encoded bodies handled.

## Consume the Spec (TypeScript)

The generated spec plugs straight into the OpenAPI TypeScript ecosystem:

```bash
npx openapi-typescript ./generated/openapi.json -o ./src/api/schema.d.ts
npm install openapi-fetch
```

```ts
import createClient from "openapi-fetch";
import type { paths } from "./api/schema";

const api = createClient<paths>({ baseUrl: "https://api.example.com" });

// Fully type-safe: path, params, body, and per-status responses all inferred.
const { data, error, response } = await api.GET("/api/tasks/{id}", {
  params: { path: { id: taskId } },
});

if (error) {
  // narrowed to NotFoundDto for the declared 404
  console.error(error.message);
}
```

Want runtime validation? Generate Zod schemas from the same spec with
[openapi-zod-client](https://github.com/astahmer/openapi-zod-client).

## Advanced Features

Rivet also supports:

- contract-driven APIs with [`[RivetContract]`](https://maxanstey-meridian.github.io/rivet/guides/contracts) — compiler-enforced single source of truth for routes, inputs, outputs, and error responses, with runtime `Invoke` helpers and [coverage checking](https://maxanstey-meridian.github.io/rivet/guides/contract-coverage)
- minimal API hosts
- file endpoints with query-string auth ([file uploads & downloads](https://maxanstey-meridian.github.io/rivet/guides/file-uploads))
- [OpenAPI import](https://maxanstey-meridian.github.io/rivet/guides/openapi-import) — a one-shot onboarding scaffold for existing APIs: it generates C# contracts once, with loud diagnostics for anything it can't represent ([supported profile](https://maxanstey-meridian.github.io/rivet/reference/import-profile)); the C# then becomes the source of truth
- [round-trips](https://maxanstey-meridian.github.io/rivet/guides/openapi-round-trips): emit → import → emit produces an equivalent spec (security scheme definitions come from `--security`, not the original spec)
- the TypeScript-first sibling project [rivet-ts](https://github.com/maxanstey-meridian/rivet-ts) (Hono runtime + the same OpenAPI pipeline via the bundled Rivet binary)

## What Rivet Enforces (and What It Doesn't)

Rivet's guarantees are **spec-time**, not runtime. The C# compiler enforces handler
input/output types on `.Invoke()`, and contract `Invoke` validates that returned
status codes (and, for ASP.NET typed results, the payload's C# type) match the
declaration. Rivet does **not** validate request or response data at runtime:

- constraint attributes (`[Range]`, `[StringLength]`, ...) flow into the spec but are
  not checked by Rivet at runtime
- response bodies are not shape-checked — extra properties on a returned object
  serialize to the wire
- `Define.File` endpoints have no runtime enforcement at all

Full statement: [Runtime Validation](https://maxanstey-meridian.github.io/rivet/guides/runtime-validation).

## Documentation

Start with:

- [Getting Started](https://maxanstey-meridian.github.io/rivet/getting-started)
- [CLI Reference](https://maxanstey-meridian.github.io/rivet/reference/cli)
- [OpenAPI Emission](https://maxanstey-meridian.github.io/rivet/guides/openapi-emission)
- [Contracts](https://maxanstey-meridian.github.io/rivet/guides/contracts)
- [OpenAPI Import](https://maxanstey-meridian.github.io/rivet/guides/openapi-import) (onboarding scaffold) and the [Import Profile](https://maxanstey-meridian.github.io/rivet/reference/import-profile)

## License

MIT
