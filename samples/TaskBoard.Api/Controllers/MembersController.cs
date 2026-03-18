using Microsoft.AspNetCore.Mvc;
using Rivet;

namespace TaskBoard.Controllers;

[RivetType]
public sealed record MemberDto(Guid Id, string Name, string Email, string Role);

[RivetType]
public sealed record InviteMemberRequest(string Email, string Role);

[RivetType]
public sealed record InviteMemberResponse(Guid Id);

[ApiController]
[Route("api/members")]
public sealed class MembersController : ControllerBase
{
    [RivetEndpoint]
    [HttpGet]
    [ProducesResponseType(typeof(List<MemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        return Ok(new List<MemberDto>());
    }

    [RivetEndpoint]
    [HttpPost]
    [ProducesResponseType(typeof(InviteMemberResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Invite(
        [FromBody] InviteMemberRequest request,
        CancellationToken ct)
    {
        return StatusCode(StatusCodes.Status201Created, new InviteMemberResponse(Guid.NewGuid()));
    }

    [RivetEndpoint]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        return Ok();
    }
}
