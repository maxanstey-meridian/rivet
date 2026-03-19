using Microsoft.AspNetCore.Mvc;
using Rivet;
using TaskBoard.Contracts;

namespace TaskBoard.Controllers;

[RivetType]
public sealed record MemberDto(Guid Id, string Name, Domain.Email Email, string Role);

[RivetType]
public sealed record InviteMemberRequest(Domain.Email Email, string Role, string Nickname);

[RivetType]
public sealed record InviteMemberResponse(Guid Id);

[RivetType]
public sealed record UpdateRoleRequest(string Role);

[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => (await MembersContract.List.Invoke(async () =>
        {
            // Must return List<MemberDto> — compiler enforced
            return new List<MemberDto>();
        })).ToActionResult();

    [HttpPost]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request, CancellationToken ct)
        => (await MembersContract.Invite.Invoke(request, async req =>
        {
            // req is InviteMemberRequest, must return InviteMemberResponse
            return new InviteMemberResponse(Guid.NewGuid());
        })).ToActionResult();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
        => (await MembersContract.Remove.Invoke(async () =>
        {
            // void endpoint — no return value
        })).ToActionResult();

    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> UpdateRole(
        Guid id, [FromBody] UpdateRoleRequest request, CancellationToken ct)
        => (await MembersContract.UpdateRole.Invoke(request, async req =>
        {
            // void — input only, 204
        })).ToActionResult();

    [HttpGet("/api/health")]
    public async Task<IActionResult> Health(CancellationToken ct)
        => (await MembersContract.Health.Invoke(async () =>
        {
            // void endpoint
        })).ToActionResult();
}

/// <summary>
/// Framework bridge — consumer writes this once per project.
/// Converts Rivet's framework-agnostic RivetResult to ASP.NET's IActionResult.
/// </summary>
public static class RivetExtensions
{
    public static IActionResult ToActionResult<T>(this RivetResult<T> result)
        => new ObjectResult(result.Data) { StatusCode = result.StatusCode };

    public static IActionResult ToActionResult(this RivetResult result)
        => new StatusCodeResult(result.StatusCode);
}
