using ContractApi.Contracts;
using ContractApi.Models;
using Microsoft.AspNetCore.Mvc;
using Rivet;

namespace ContractApi.Controllers;

[ApiController]
public sealed class MembersController : ControllerBase
{
    [HttpGet(MembersContract.ListRoute)]
    public IActionResult List(CancellationToken _)
    {
        // Must return PagedResult<MemberDto> — compiler enforced
        var members = new List<MemberDto>();
        return MembersContract
            .List.Success(new PagedResult<MemberDto>(members, members.Count))
            .ToActionResult();
    }

    [HttpPost(MembersContract.InviteRoute)]
    public IActionResult Invite([FromBody] InviteMemberRequest request, CancellationToken _)
    {
        var endpoint = MembersContract.Invite.Bind(request);
        return endpoint.Success(new InviteMemberResponse(Guid.NewGuid())).ToActionResult();
    }

    [HttpDelete(MembersContract.RemoveRoute)]
    public IActionResult Remove(Guid id, CancellationToken _) =>
        MembersContract.Remove.Bind(new RemoveMemberInput(id)).Success().ToActionResult();

    [HttpPut(MembersContract.UpdateRoleRoute)]
    public IActionResult UpdateRole(
        Guid id,
        [FromBody] UpdateRoleRequest request,
        CancellationToken _
    ) =>
        MembersContract
            .UpdateRole.Bind(new UpdateRoleInput { Id = id, Role = request.Role })
            .Success()
            .ToActionResult();

    [HttpGet(MembersContract.HealthRoute)]
    public IActionResult Health(CancellationToken _) =>
        MembersContract.Health.Success().ToActionResult();
}
