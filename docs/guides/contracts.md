# Contracts

A contract is a `static class` marked `[RivetContract]` whose `static readonly`
fields define endpoints via the `Define` factory. Contracts are plain C# — the
`Rivet.Attributes` package has no hard ASP.NET hosting dependency in your contract
code — and they serve two roles:

1. **Generation time**: Roslyn reads the `Define` chain and emits the OpenAPI
   operation.
2. **Runtime**: controllers execute the same definition through `.Invoke()`, so the
   handler's input/output types are compiler-enforced against the contract.

## Defining a contract

This is the `samples/ContractApi` contract, trimmed:

```csharp
using Rivet;

[RivetContract]
public static class MembersContract
{
    public static readonly RouteDefinition<PagedResult<MemberDto>> List =
        Define.Get<PagedResult<MemberDto>>("/api/members")
            .Description("List all team members");

    public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
        Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Description("Invite a new team member")
            .Status(201)
            .Returns<ValidationErrorDto>(422, "Validation failed")
            .Secure("admin");

    public static readonly RouteDefinition Remove =
        Define.Delete("/api/members/{id}")
            .Returns<NotFoundDto>(404, "Member not found")
            .Secure("admin");

    // Input only, 204 — chain .Accepts<T>() from a void definition
    public static readonly InputRouteDefinition<UpdateRoleRequest> UpdateRole =
        Define.Put("/api/members/{id}/role")
            .Accepts<UpdateRoleRequest>()
            .Status(204)
            .Returns<NotFoundDto>(404, "Member not found");

    public static readonly RouteDefinition Health =
        Define.Get("/api/health").Anonymous();
}
```

`Define.Get/Post/Put/Patch/Delete` come in three arities: `<TInput, TOutput>`,
`<TOutput>`, and untyped (void). Default success statuses: `200` for GET/PUT/PATCH,
`201` for POST, `204` for void DELETE (typed DELETE defaults to `200` — a 204 with a
body is invalid HTTP). `Define.File` creates binary/stream endpoints — see
[File Uploads](/guides/file-uploads).

The full builder surface (`.Status()`, `.Returns()`, `.Secure()`, `.Anonymous()`,
`.QueryAuth()`, `.FormEncoded()`, `.AcceptsFile()`, ...) is documented in the
[Route Definition API](/reference/endpoint-builder).

## Implementing a contract

Controllers (or minimal API handlers) execute the contract with `.Invoke()`:

```csharp
[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request, CancellationToken ct)
        => (await MembersContract.Invite.Invoke(request, async req =>
        {
            // req is InviteMemberRequest; must return InviteMemberResponse — compiler enforced
            return new InviteMemberResponse(Guid.NewGuid());
        })).ToActionResult();
}
```

`Invoke` returns a framework-agnostic `RivetResult` / `RivetResult<T>` carrying the
declared success status. You write a small bridge once per project:

```csharp
public static class RivetExtensions
{
    public static IActionResult ToActionResult<T>(this RivetResult<T> result)
        => new ObjectResult(result.Data) { StatusCode = result.StatusCode };

    public static IActionResult ToActionResult(this RivetResult result)
        => new StatusCodeResult(result.StatusCode);

    // Minimal API bridge
    public static IResult ToResult<T>(this RivetResult<T> result)
        => Results.Json(result.Data, statusCode: result.StatusCode);

    public static IResult ToResult(this RivetResult result)
        => Results.StatusCode(result.StatusCode);
}
```

Minimal APIs work the same way — `.Route` and `.Method` are available at runtime:

```csharp
app.MapGet(MembersContract.Health.Route, async () =>
    (await MembersContract.Health.Invoke(async () => { })).ToResult());
```

## Typed results and multiple statuses

When an endpoint declares error responses, return ASP.NET `Results<...>` from the
handler. Rivet validates at request time that the returned status code and payload
C# type match a declared response, and throws `InvalidOperationException` otherwise.
Endpoints returning framework results without a status code (e.g.
`ChallengeHttpResult`) need `.SkipValidation()`.

See [Runtime Validation](/guides/runtime-validation) for the precise scope of what
is and isn't enforced at runtime — in short: status codes and C# payload types on
the typed-results path, **not** response body shape and **not** constraint
attributes.

## Definitions are immutable once used

Contract definitions live in shared static fields. After the first `Invoke`, all
builder methods throw — configure the definition fully in its field initializer.

## Verifying coverage

`dotnet rivet --project … --check` confirms every contract field has an
implementation whose route and method match. See
[Contract Coverage](/guides/contract-coverage).
