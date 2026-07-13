# Contracts

A contract is a `static class` marked `[RivetContract]` whose `static readonly`
fields define endpoints via the `Define` factory. Contracts are plain C# — the
`Rivet.Attributes` package has no hard ASP.NET hosting dependency in your contract
code — and they serve two roles:

1. **Generation time**: Roslyn reads the `Define` chain and emits the OpenAPI
   operation.
2. **Runtime**: controllers use the same definition to bind inputs and construct
   responses, so input/output types and selected statuses are enforced against the
   contract.

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

For an input-bearing endpoint, bind the transport input, execute ordinary
application code, then construct the response with `.Success(...)`, `.Error(...)`,
or `.File(...)`:

```csharp
[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request, CancellationToken ct)
    {
        var endpoint = MembersContract.Invite.Bind(request);
        var response = await memberService.Invite(request, ct);

        // Must be InviteMemberResponse — compiler enforced
        return endpoint.Success(response).ToActionResult();
    }
}
```

`Bind` is required only for definitions with `TInput`; it returns a bound endpoint
whose terminal methods retain the contract's output type. Definitions without input
call the same terminal methods directly. Application services remain ordinary C# —
Rivet does not own their invocation.

The terminal methods return a framework-agnostic `RivetResult`. Rivet provides the
host bridges: `.ToActionResult()` for MVC controllers and `.ToResult()` for minimal
APIs.

Minimal APIs work the same way — `.Route` and `.Method` are available at runtime:

```csharp
app.MapGet(MembersContract.Health.Route, () =>
    MembersContract.Health.Success().ToResult());
```

## Success, errors, and files

`.Success(...)` selects the declared success response. `.Error(status, ...)` selects
a response declared with `.Returns(...)`; undeclared statuses, missing or unexpected
payloads, and incompatible payload types throw `RivetContractViolationException`.
`.File(...)` constructs a binary response for `Define.File` or `.ProducesFile(...)`
definitions. Each result is adapted to ASP.NET only at the final
`.ToActionResult()` / `.ToResult()` boundary.

See [Runtime Validation](/guides/runtime-validation) for the precise scope of what
is and isn't enforced at runtime — in short: declared statuses, C# payload types,
body presence, and content representations, **not** serialized response shape or
constraint attributes.

## Definitions are immutable once used

Contract definitions live in shared static fields. The first `Bind`, `Success`,
`Error`, or `File` publishes the definition; after that all builder methods throw.
Configure the definition fully in its field initializer.

## Verifying coverage

`dotnet rivet --project … --check` confirms every contract field has an
implementation whose route and method match. See
[Contract Coverage](/guides/contract-coverage).
