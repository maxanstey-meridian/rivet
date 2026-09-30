<p align="center">
  <h1 align="center">Rivet</h1>
  <p align="center">
    <a href="https://www.nuget.org/packages/Rivet.Attributes"><img src="https://img.shields.io/nuget/v/Rivet.Attributes?label=Rivet.Attributes" alt="NuGet" /></a>
    <a href="https://www.nuget.org/packages/dotnet-rivet"><img src="https://img.shields.io/nuget/v/dotnet-rivet?label=dotnet-rivet" alt="NuGet" /></a>
    <img src="https://img.shields.io/badge/license-MIT-blue" alt="License" />
  </p>
</p>

**Your C# is the contract.** Rivet reads your compiled C# with Roslyn and
deterministically emits an OpenAPI 3.1 spec from its declared transport shape,
with diagnostics for known fidelity loss. There is no runtime reflection and no
need for attributes on every member. The OpenAPI ecosystem does the rest:
TypeScript types, a typed fetch client, Zod schemas, rendered docs.

[oRPC](https://orpc.unnoq.com) gives you this when your server is TypeScript.
Rivet gives you the same DX when your server is .NET.

## Prerequisites

- .NET 8 SDK or later for your API project (`Rivet.Attributes` targets net8.0, net9.0 and net10.0).
- .NET 9 or later for the `dotnet-rivet` tool: the SDK, or both the .NET and ASP.NET Core runtimes.
- Node.js, only for the TypeScript steps under [Consume](#consume).

## Install

```bash
dotnet new webapi -n Api --use-controllers   # or skip this and use an existing ASP.NET Core project
cd Api
dotnet add package Rivet.Attributes
dotnet tool install --global dotnet-rivet
```

## Two ways in

### Already have an ASP.NET API? Annotate it.

Mark the endpoints you want surfaced — the operation is derived from what you
explicitly declare (routes, `[FromBody]`/`[FromQuery]`/... bindings,
`[ProducesResponseType]` entries) plus a few documented narrow conventions, not
from a reconstruction of MVC's model-binding defaults:

```csharp
using Microsoft.AspNetCore.Mvc;
using Rivet;

public sealed record TaskDetailDto(Guid Id, string Title);

public sealed record NotFoundDto(string Message);

[ApiController]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [RivetEndpoint]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
    public IActionResult Get(Guid id) =>
        id == Guid.Empty
            ? NotFound(new NotFoundDto("Task not found"))
            : Ok(new TaskDetailDto(id, "Write the docs"));
}
```

That becomes `GET /api/tasks/{id}` with a typed `200` and `404` — route
constraints normalised, params classified, multipart and form bodies handled.

### Starting fresh? Write the contract first.

A contract is plain C#: routes, inputs, outputs, and error responses in one
place, as data:

```csharp
using Rivet;

public sealed record MemberDto(Guid Id, string Email);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public sealed record InviteMemberRequest(string Email);

public sealed record InviteMemberResponse(Guid Id);

public sealed record ValidationErrorDto(string Message);

[RivetContract]
public static class MembersContract
{
    public static readonly RouteDefinition<PagedResult<MemberDto>> List =
        Define.Get<PagedResult<MemberDto>>("/api/members");

    public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
        Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Status(201)
            .Returns<ValidationErrorDto>(422, "Validation failed")
            .Secure("admin");
}
```

At the transport boundary, bind the declared input, run ordinary application code,
then construct the response through the contract. The compiler enforces the input
and output types; Rivet validates the selected response at runtime. `IMemberService`
stands in for your application code; register an implementation before calling it:

```csharp
using Microsoft.AspNetCore.Mvc;
using Rivet;

public interface IMemberService
{
    Task<InviteMemberResponse> Invite(InviteMemberRequest request, CancellationToken ct);
}

[ApiController]
[Route("api/members")]
public sealed class MembersController(IMemberService memberService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request,
        CancellationToken ct
    )
    {
        var endpoint = MembersContract.Invite.Bind(request);
        var response = await memberService.Invite(request, ct);

        // Must be InviteMemberResponse — compiler-enforced
        return endpoint.Success(response).ToActionResult();
    }
}
```

Either way — annotated endpoints, contracts, or a mix — the spec comes out the same.

## Generate

```bash
dotnet rivet --project Api.csproj --output ./generated --security admin=bearer
```

Writes `./generated/openapi.json`, derived from the compiled C# via the Roslyn
semantic model. Rivet reads explicit contract declarations and explicit ASP.NET
transport metadata, with deliberately tiny documented conventions — value-object
brands are opt-in via `[RivetScalar]`, enums are numeric unless a type-level
`JsonStringEnumConverter` declares them string-valued. Generics, nullability,
validation attributes, polymorphic hierarchies (`oneOf` + discriminator),
dictionary key types, headers, descriptions, and examples all flow into the
spec.

`admin=bearer` defines the bearer scheme that `.Secure("admin")` names. The first
`--security` is also the document-wide default, so every endpoint without
`.Anonymous()` requires it, `GET /api/tasks/{id}` included. Drop the flag and the
`.Secure("admin")` call for an unauthenticated API, or see the
[CLI reference](https://maxanstey-meridian.github.io/rivet/reference/cli) for
multiple schemes.

## Consume

The spec plugs straight into the OpenAPI TypeScript ecosystem:

```bash
npx openapi-typescript ./generated/openapi.json -o ./src/api/schema.d.ts
npm install openapi-fetch
```

```ts
import createClient from "openapi-fetch";
import type { paths } from "./api/schema";

const api = createClient<paths>({ baseUrl: "https://api.example.com" });
const taskId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

// Path, params, body, and per-status responses all inferred.
const { data, error } = await api.GET("/api/tasks/{id}", {
  params: { path: { id: taskId } },
});

if (error) {
  // narrowed to NotFoundDto for the declared 404
  console.error(error.message);
}
```

Docs via any OpenAPI renderer; runtime validators via
[openapi-zod-client](https://github.com/astahmer/openapi-zod-client) if you want them.

## Also in the box

- [Azure Functions sample](samples/FunctionsApi/README.md) and [integration guide](docs/guides/azure-functions.md) — isolated-worker routes, per-response file MIME selection, and `task test:functions` against the real host

- [Contract coverage checking](https://maxanstey-meridian.github.io/rivet/guides/contract-coverage) — `--check` verifies every contract field has an implementation on the declared route and method
- [OpenAPI import](https://maxanstey-meridian.github.io/rivet/guides/openapi-import) — one-shot onboarding for existing APIs: generate C# contracts from a spec, then the C# is the source of truth
- [File endpoints](https://maxanstey-meridian.github.io/rivet/guides/file-uploads), headers as contract concepts, minimal-API hosts, [round-trippable specs](https://maxanstey-meridian.github.io/rivet/guides/openapi-round-trips)
- Stable `RIVnnnn` [diagnostic IDs](https://maxanstey-meridian.github.io/rivet/reference/diagnostics) on every warning — grep or baseline by ID
- A TypeScript-first sibling, [rivet-ts](https://github.com/maxanstey-meridian/rivet-ts) — same pipeline, contracts authored as TS types, Hono runtime

## Documentation

[Getting Started](https://maxanstey-meridian.github.io/rivet/getting-started) ·
[Contracts](https://maxanstey-meridian.github.io/rivet/guides/contracts) ·
[CLI Reference](https://maxanstey-meridian.github.io/rivet/reference/cli) ·
[Type Mapping](https://maxanstey-meridian.github.io/rivet/reference/type-mapping) ·
[Runtime Validation](https://maxanstey-meridian.github.io/rivet/guides/runtime-validation)
(the precise scope of what is and isn't enforced at runtime)

## License

[MIT](LICENSE)
