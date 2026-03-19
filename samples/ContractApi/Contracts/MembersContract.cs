using Rivet;
using TaskBoard.Controllers;
using TaskBoard.Domain;
using Endpoint = Rivet.Endpoint;
using EndpointBuilder = Rivet.EndpointBuilder;

namespace TaskBoard.Contracts;

/// <summary>
/// Contract-driven endpoint definitions for the Members API.
/// Pure Rivet — no ASP.NET dependency. Controllers use .Invoke() for
/// type-safe execution with compiler-enforced input/output types.
/// </summary>
[RivetContract]
public static class MembersContract
{
    public static readonly EndpointBuilder<List<MemberDto>> List =
        Endpoint.Get<List<MemberDto>>("/api/members")
            .Description("List all team members");

    public static readonly EndpointBuilder<InviteMemberRequest, InviteMemberResponse> Invite =
        Endpoint.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Description("Invite a new team member")
            .Status(201)
            .Returns<InviteMemberResponse>(422, "Validation failed")
            .Secure("admin");

    public static readonly EndpointBuilder Remove =
        Endpoint.Delete("/api/members/{id}")
            .Description("Remove a team member")
            .Returns<MemberDto>(404, "Member not found")
            .Secure("admin");

    public static readonly EndpointBuilder Health =
        Endpoint.Get("/api/health")
            .Description("Health check")
            .Anonymous();
}
