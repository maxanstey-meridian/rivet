using Rivet;
using TaskBoard.Controllers;
using Endpoint = Rivet.Endpoint;

namespace TaskBoard.Contracts;

/// <summary>
/// Contract-driven endpoint definitions for the Members API.
/// Rivet reads these at generation time — nothing here executes at runtime.
/// </summary>
[RivetContract]
public static class MembersContract
{
    public static readonly Endpoint List =
        Endpoint.Get<MemberDto>("/api/members")
            .Description("List all team members");

    public static readonly Endpoint Invite =
        Endpoint.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Description("Invite a new team member")
            .Status(201)
            .Returns<InviteMemberResponse>(422, "Validation failed");

    public static readonly Endpoint Remove =
        Endpoint.Delete("/api/members/{id}")
            .Description("Remove a team member")
            .Returns<MemberDto>(404, "Member not found");
}
