using Microsoft.AspNetCore.Mvc;
using Rivet;
using TaskBoard.Domain;

namespace TaskBoard.Controllers;

[RivetType]
public sealed record MemberDto(Guid Id, string Name, Email Email, string Role);

[RivetType]
public sealed record InviteMemberRequest(Email Email, string Role);

[RivetType]
public sealed record InviteMemberResponse(Guid Id);

[RivetClient]
[ApiController]
[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<MemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        return Ok(new List<MemberDto>());
    }

    [HttpPost]
    [ProducesResponseType(typeof(InviteMemberResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request,
        CancellationToken ct)
    {
        return StatusCode(StatusCodes.Status201Created, new InviteMemberResponse(Guid.NewGuid()));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        return Ok();
    }
}
