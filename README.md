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

```bash
dotnet add package Rivet.Attributes
dotnet tool install --global dotnet-rivet
```

## Two ways in

### Already have an ASP.NET API? Annotate it.

Mark the endpoints you want surfaced — the operation is derived from the real
transport shape (routes, params, bodies, response types):

```csharp
[ApiController]
[Route("api/tasks")]
public sealed class TasksController : ControllerBase
{
    [RivetEndpoint]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) { ... }
}
```

That becomes `GET /api/tasks/{id}` with a typed `200` and `404` — route
constraints normalised, params classified, multipart and form bodies handled.

### Starting fresh? Write the contract first.

A contract is plain C#: routes, inputs, outputs, and error responses in one
place, as data:

```csharp
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
and output types; Rivet validates the selected response at runtime:

```csharp
[HttpPost]
public async Task<IActionResult> Invite([FromBody] InviteMemberRequest request, CancellationToken ct)
{
    var endpoint = MembersContract.Invite.Bind(request);
    var response = await memberService.Invite(request, ct);

    // Must be InviteMemberResponse — compiler-enforced
    return endpoint.Success(response).ToActionResult();
}
```

Either way — annotated endpoints, contracts, or a mix — the spec comes out the same.

## Generate

```bash
dotnet rivet --project path/to/Api.csproj --output ./generated --security admin=bearer
```

Writes `./generated/openapi.json`, derived from the compiled C# via the Roslyn
semantic model. Value-object brands, generics, nullability, validation
attributes, polymorphic hierarchies (`oneOf` + discriminator), dictionary key
types, headers, descriptions, and examples all flow into the spec.

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
