using Microsoft.AspNetCore.Mvc;
using Rivet;
using ContractApi.Contracts;
using ContractApi.Models;

namespace ContractApi.Controllers;

[ApiController]
public sealed class MembersController : ControllerBase
{
    [HttpGet(MembersContract.ListRoute)]
    public async Task<IActionResult> List(CancellationToken ct)
        => (await MembersContract.List.Invoke(async () =>
        {
            // Must return PagedResult<MemberDto> — compiler enforced
            var members = new List<MemberDto>();
            return new PagedResult<MemberDto>(members, members.Count);
        })).ToActionResult();

    [HttpPost(MembersContract.InviteRoute)]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request, CancellationToken ct)
        => (await MembersContract.Invite.Invoke(request, async req =>
        {
            // req is InviteMemberRequest, must return InviteMemberResponse
            return new InviteMemberResponse(Guid.NewGuid());
        })).ToActionResult();

    [HttpDelete(MembersContract.RemoveRoute)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
        => (await MembersContract.Remove.Invoke(new RemoveMemberInput(id), async input =>
        {
            // void endpoint — no return value
        })).ToActionResult();

    [HttpPut(MembersContract.UpdateRoleRoute)]
    public async Task<IActionResult> UpdateRole(
        Guid id, [FromBody] UpdateRoleRequest request, CancellationToken ct)
        => (await MembersContract.UpdateRole.Invoke(new UpdateRoleInput { Id = id, Role = request.Role }, async input =>
        {
            // void — input only, 204
        })).ToActionResult();

    [HttpGet(MembersContract.HealthRoute)]
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

    // Minimal API bridge
    public static IResult ToResult<T>(this RivetResult<T> result)
        => Results.Json(result.Data, statusCode: result.StatusCode);

    public static IResult ToResult(this RivetResult result)
        => Results.StatusCode(result.StatusCode);
}
