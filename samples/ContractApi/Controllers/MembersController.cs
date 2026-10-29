using ContractApi.Contracts;
using ContractApi.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rivet;

namespace ContractApi.Controllers;

[ApiController]
public sealed class MembersController : ControllerBase
{
    // A 1x1 JPEG: the sample has no avatar store, so every member wears the same pixel.
    private static readonly byte[] _placeholderAvatar = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA="
    );

    // Contracts/Security.cs makes bearer the document's default, so List needs a caller too.
    [Authorize]
    [HttpGet(MembersContract.ListRoute)]
    public IActionResult List(CancellationToken _)
    {
        // Must return PagedResult<MemberDto> — compiler enforced
        var members = new List<MemberDto>();
        return MembersContract
            .List.Success(new PagedResult<MemberDto>(members, members.Count))
            .ToActionResult();
    }

    // The contract says .Secure("bearer"), so the handler requires an authenticated
    // caller; --check holds the two together (RIV4008).
    [Authorize]
    [HttpPost(MembersContract.InviteRoute)]
    public IActionResult Invite([FromBody] InviteMemberRequest request, CancellationToken _)
    {
        var endpoint = MembersContract.Invite.Bind(request);
        return endpoint.Success(new InviteMemberResponse(Guid.NewGuid())).ToActionResult();
    }

    [Authorize]
    [HttpDelete(MembersContract.RemoveRoute)]
    public IActionResult Remove(Guid id, CancellationToken _) =>
        MembersContract.Remove.Bind(new RemoveMemberInput(id)).Success().ToActionResult();

    [Authorize]
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

    // .QueryAuth(): a media player can't set a header, so the token arrives as ?token=,
    // which the sample's bearer handler reads too.
    [Authorize]
    [HttpGet(MembersContract.AvatarRoute)]
    public IActionResult Avatar(Guid id, CancellationToken _) =>
        MembersContract
            .Avatar.File(_placeholderAvatar, downloadName: $"{id}.jpg", inline: true)
            .ToActionResult();
}
